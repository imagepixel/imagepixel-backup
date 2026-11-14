//go:build windows

// Package winmount serves the snapshots of a repository as a read-only volume
// on Windows. It is built on WinFsp through the cgofuse bindings.
//
// The volume has one directory per snapshot in its root. Everything below is
// loaded from the repository on demand. Every operation that would modify the
// volume is rejected, so the repository cannot be changed through the mount.
package winmount

import (
	"context"
	"fmt"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/hashicorp/golang-lru/v2/simplelru"
	"github.com/winfsp/cgofuse/fuse"
	"golang.org/x/sys/windows"

	"github.com/restic/restic/internal/bloblru"
	"github.com/restic/restic/internal/data"
	"github.com/restic/restic/internal/debug"
	"github.com/restic/restic/internal/errors"
	"github.com/restic/restic/internal/restic"
)

const (
	// DefaultTimeTemplate names snapshot directories. It must not contain
	// characters that are invalid in Windows file names, such as colons.
	DefaultTimeTemplate = "2006-01-02_15-04-05"

	// DefaultVolumeName is the label of the mounted volume.
	DefaultVolumeName = "restic"

	blobCacheSize          = 64 << 20
	dirCacheEntries        = 512
	blockSize              = 4096
	maxNameLength          = 255
	maxVolumeNameLength    = 32
	snapshotReloadInterval = 60 * time.Second

	noHandle = ^uint64(0)
)

// Config holds the settings of a mount.
type Config struct {
	// Filter selects the snapshots that are shown.
	Filter data.SnapshotFilter
	// TimeTemplate is the time.Format layout used to name snapshot directories.
	TimeTemplate string
	// VolumeName is the label of the volume as shown by Windows.
	VolumeName string
	// Ready is called once the volume is mounted, if it is not nil.
	Ready func()
}

// directory is the listing of one directory.
type directory struct {
	names  []string
	nodes  map[string]*data.Node
	folded map[string]*data.Node
}

func newDirectory() *directory {
	return &directory{
		nodes:  make(map[string]*data.Node),
		folded: make(map[string]*data.Node),
	}
}

func (d *directory) add(name string, node *data.Node) {
	if _, exists := d.nodes[name]; exists {
		return
	}
	d.names = append(d.names, name)
	d.nodes[name] = node
	key := strings.ToLower(name)
	if _, exists := d.folded[key]; !exists {
		d.folded[key] = node
	}
}

// lookup finds an entry by name. Windows expects names to be case-insensitive,
// so an exact match is preferred and a case-insensitive match is the fallback.
func (d *directory) lookup(name string) (*data.Node, bool) {
	if node, ok := d.nodes[name]; ok {
		return node, true
	}
	node, ok := d.folded[strings.ToLower(name)]
	return node, ok
}

// openFile is the state of an open file.
type openFile struct {
	node *data.Node

	once sync.Once
	err  error
	// cumsize[i] holds the cumulative size of node.Content[:i].
	cumsize []uint64
}

// FS is a read-only file system that exposes the snapshots of a repository.
type FS struct {
	fuse.FileSystemBase

	ctx  context.Context
	repo restic.Repository
	cfg  Config

	blobCache *bloblru.Cache
	rootNode  *data.Node

	ready     chan struct{}
	readyOnce sync.Once

	mu        sync.Mutex
	snapshots *directory
	totalSize uint64
	lastLoad  time.Time
	dirs      *simplelru.LRU[restic.ID, *directory]
	handles   map[uint64]*openFile
	lastFh    uint64
}

// New creates the file system for repo and loads the list of snapshots.
// The index of repo must have been loaded already.
func New(ctx context.Context, repo restic.Repository, cfg Config) (*FS, error) {
	if cfg.TimeTemplate == "" {
		cfg.TimeTemplate = DefaultTimeTemplate
	}
	if cfg.VolumeName == "" {
		cfg.VolumeName = DefaultVolumeName
	}

	dirs, err := simplelru.NewLRU[restic.ID, *directory](dirCacheEntries, nil)
	if err != nil {
		return nil, err
	}

	now := time.Now()
	f := &FS{
		ctx:       ctx,
		repo:      repo,
		cfg:       cfg,
		blobCache: bloblru.New(blobCacheSize),
		rootNode: &data.Node{
			Type:       data.NodeTypeDir,
			Mode:       os.ModeDir | 0555,
			ModTime:    now,
			AccessTime: now,
			ChangeTime: now,
		},
		ready:     make(chan struct{}),
		snapshots: newDirectory(),
		dirs:      dirs,
		handles:   make(map[uint64]*openFile),
	}

	if err := f.loadSnapshots(); err != nil {
		return nil, err
	}
	return f, nil
}

// loadSnapshots rebuilds the root directory from the snapshots in the repository.
func (f *FS) loadSnapshots() error {
	var snapshots data.Snapshots
	err := f.cfg.Filter.FindAll(f.ctx, f.repo, f.repo, nil, func(_ string, sn *data.Snapshot, _ error) error {
		if sn != nil {
			snapshots = append(snapshots, sn)
		}
		return nil
	})
	if err != nil {
		return err
	}

	// Sort ascending by time and use the ID to break ties, so that the
	// directory names are stable across reloads.
	sort.Slice(snapshots, func(i, j int) bool {
		si, sj := snapshots[i], snapshots[j]
		if si.Time.Equal(sj.Time) {
			return si.ID().String() < sj.ID().String()
		}
		return si.Time.Before(sj.Time)
	})

	root := newDirectory()
	var total uint64
	for _, sn := range snapshots {
		if sn.Tree == nil {
			continue
		}

		base := cleanName(sn.Time.Local().Format(f.cfg.TimeTemplate))
		name := base
		for i := 1; ; i++ {
			if _, taken := root.lookup(name); !taken {
				break
			}
			name = fmt.Sprintf("%s-%d", base, i)
		}

		root.add(name, &data.Node{
			Name:       name,
			Type:       data.NodeTypeDir,
			Mode:       os.ModeDir | 0555,
			ModTime:    sn.Time,
			AccessTime: sn.Time,
			ChangeTime: sn.Time,
			Subtree:    sn.Tree,
		})

		if sn.Summary != nil && sn.Summary.TotalBytesProcessed > total {
			total = sn.Summary.TotalBytesProcessed
		}
	}
	sort.Strings(root.names)

	f.mu.Lock()
	f.snapshots = root
	f.totalSize = total
	f.lastLoad = time.Now()
	f.mu.Unlock()
	return nil
}

// refreshSnapshots reloads the list of snapshots if it is older than
// snapshotReloadInterval. A failed reload keeps the previous list.
func (f *FS) refreshSnapshots() {
	f.mu.Lock()
	stale := time.Since(f.lastLoad) >= snapshotReloadInterval
	if stale {
		// prevent concurrent callers from starting another reload
		f.lastLoad = time.Now()
	}
	f.mu.Unlock()

	if !stale {
		return
	}
	if err := f.loadSnapshots(); err != nil {
		debug.Log("reloading snapshots failed: %v", err)
	}
}

// cleanName replaces the characters that Windows does not allow in file names.
func cleanName(name string) string {
	name = strings.Map(func(r rune) rune {
		if r < 0x20 || strings.ContainsRune(`<>:"/\|?*`, r) {
			return '_'
		}
		return r
	}, name)
	name = strings.TrimRight(name, " .")
	if name == "" {
		return "_"
	}
	return name
}

// loadDir returns the listing of the tree with the given ID.
func (f *FS) loadDir(id restic.ID) (*directory, error) {
	f.mu.Lock()
	dir, ok := f.dirs.Get(id)
	f.mu.Unlock()
	if ok {
		return dir, nil
	}

	dir = newDirectory()
	tree, err := data.LoadTree(f.ctx, f.repo, id)
	if err != nil {
		return nil, err
	}
	for item := range tree {
		if item.Error != nil {
			return nil, item.Error
		}
		if err := f.addNode(dir, item.Node); err != nil {
			return nil, err
		}
	}
	sort.Strings(dir.names)

	f.mu.Lock()
	f.dirs.Add(id, dir)
	f.mu.Unlock()
	return dir, nil
}

// addNode adds node to dir. Directories named "." or "/" are replaced by their
// contents, which matches the behaviour of the FUSE mount on other platforms.
func (f *FS) addNode(dir *directory, node *data.Node) error {
	if node.Type == data.NodeTypeDir && node.Subtree != nil && (node.Name == "." || node.Name == "/") {
		tree, err := data.LoadTree(f.ctx, f.repo, *node.Subtree)
		if err != nil {
			return err
		}
		for item := range tree {
			if item.Error != nil {
				return item.Error
			}
			f.addEntry(dir, item.Node)
		}
		return nil
	}

	f.addEntry(dir, node)
	return nil
}

func (f *FS) addEntry(dir *directory, node *data.Node) {
	switch node.Type {
	case data.NodeTypeDir, data.NodeTypeFile, data.NodeTypeSymlink:
		// path.Base is used on purpose: filepath.Base would strip everything
		// up to a colon, which is a valid character in names from other systems.
		dir.add(path.Base(node.Name), node)
	default:
		// devices, sockets and pipes have no meaning on a Windows volume
		debug.Log("skipping %v of type %v", node.Name, node.Type)
	}
}

// children returns the listing of the directory node.
func (f *FS) children(node *data.Node) (*directory, int) {
	if node == f.rootNode {
		f.mu.Lock()
		defer f.mu.Unlock()
		return f.snapshots, 0
	}
	if node.Type != data.NodeTypeDir {
		return nil, -fuse.ENOTDIR
	}
	if node.Subtree == nil {
		return newDirectory(), 0
	}

	dir, err := f.loadDir(*node.Subtree)
	if err != nil {
		debug.Log("loading tree %v failed: %v", node.Subtree, err)
		return nil, -fuse.EIO
	}
	return dir, 0
}

// lookup resolves a path of the volume to its node.
func (f *FS) lookup(name string) (*data.Node, int) {
	node := f.rootNode
	for _, part := range strings.Split(name, "/") {
		if part == "" {
			continue
		}
		dir, errc := f.children(node)
		if errc != 0 {
			return nil, errc
		}
		child, ok := dir.lookup(part)
		if !ok {
			return nil, -fuse.ENOENT
		}
		node = child
	}
	return node, 0
}

// fillStat converts node to the attributes reported to Windows. The
// permissions never include write access, whatever was stored in the backup.
func fillStat(node *data.Node, st *fuse.Stat_t) {
	*st = fuse.Stat_t{}

	switch node.Type {
	case data.NodeTypeDir:
		st.Mode = fuse.S_IFDIR | 0555
		st.Nlink = 2
	case data.NodeTypeSymlink:
		st.Mode = fuse.S_IFLNK | 0444
		st.Nlink = 1
		st.Size = int64(len(node.LinkTarget))
	default:
		st.Mode = fuse.S_IFREG | 0444
		st.Nlink = 1
		st.Size = int64(node.Size)
	}

	st.Blksize = blockSize
	st.Blocks = (st.Size + 511) / 512

	mtime := node.ModTime
	atime := node.AccessTime
	if atime.IsZero() {
		atime = mtime
	}
	ctime := node.ChangeTime
	if ctime.IsZero() {
		ctime = mtime
	}
	st.Mtim = fuse.NewTimespec(mtime)
	st.Atim = fuse.NewTimespec(atime)
	st.Ctim = fuse.NewTimespec(ctime)
	st.Birthtim = fuse.NewTimespec(mtime)
}

// Init is called by the host once the volume is mounted.
func (f *FS) Init() {
	f.readyOnce.Do(func() {
		close(f.ready)
		if f.cfg.Ready != nil {
			f.cfg.Ready()
		}
	})
}

// Statfs reports the size of the volume. There is never any free space.
func (f *FS) Statfs(_ string, stat *fuse.Statfs_t) int {
	f.mu.Lock()
	total := f.totalSize
	f.mu.Unlock()

	blocks := (total + blockSize - 1) / blockSize
	if blocks == 0 {
		blocks = 1
	}
	*stat = fuse.Statfs_t{
		Bsize:   blockSize,
		Frsize:  blockSize,
		Blocks:  blocks,
		Namemax: maxNameLength,
	}
	return 0
}

// Getattr returns the attributes of a file or directory.
func (f *FS) Getattr(name string, stat *fuse.Stat_t, _ uint64) int {
	node, errc := f.lookup(name)
	if errc != 0 {
		return errc
	}
	fillStat(node, stat)
	return 0
}

// Access denies every request for write or delete access.
func (f *FS) Access(name string, mask uint32) int {
	if _, errc := f.lookup(name); errc != 0 {
		return errc
	}
	if mask&(fuse.W_OK|fuse.DELETE_OK) != 0 {
		return -fuse.EROFS
	}
	return 0
}

// Readlink returns the target of a symbolic link.
func (f *FS) Readlink(name string) (int, string) {
	node, errc := f.lookup(name)
	if errc != 0 {
		return errc, ""
	}
	if node.Type != data.NodeTypeSymlink {
		return -fuse.EINVAL, ""
	}
	return 0, filepath.ToSlash(node.LinkTarget)
}

// Opendir opens a directory.
func (f *FS) Opendir(name string) (int, uint64) {
	node, errc := f.lookup(name)
	if errc != 0 {
		return errc, noHandle
	}
	if node.Type != data.NodeTypeDir {
		return -fuse.ENOTDIR, noHandle
	}
	if node == f.rootNode {
		f.refreshSnapshots()
	}
	return 0, 0
}

// Readdir lists a directory.
func (f *FS) Readdir(name string, fill func(name string, stat *fuse.Stat_t, ofst int64) bool, _ int64, _ uint64) int {
	node, errc := f.lookup(name)
	if errc != 0 {
		return errc
	}
	dir, errc := f.children(node)
	if errc != 0 {
		return errc
	}

	var st fuse.Stat_t
	fillStat(node, &st)
	fill(".", &st, 0)
	fill("..", nil, 0)
	for _, entry := range dir.names {
		fillStat(dir.nodes[entry], &st)
		if !fill(entry, &st, 0) {
			break
		}
	}
	return 0
}

// Releasedir closes a directory.
func (f *FS) Releasedir(_ string, _ uint64) int {
	return 0
}

// Open opens a file for reading. Any other access mode is rejected.
func (f *FS) Open(name string, flags int) (int, uint64) {
	if flags&fuse.O_ACCMODE != fuse.O_RDONLY || flags&(fuse.O_APPEND|fuse.O_CREAT|fuse.O_TRUNC) != 0 {
		return -fuse.EROFS, noHandle
	}

	node, errc := f.lookup(name)
	if errc != 0 {
		return errc, noHandle
	}
	if node.Type == data.NodeTypeDir {
		return -fuse.EISDIR, noHandle
	}

	f.mu.Lock()
	f.lastFh++
	fh := f.lastFh
	f.handles[fh] = &openFile{node: node}
	f.mu.Unlock()
	return 0, fh
}

// Release closes a file.
func (f *FS) Release(_ string, fh uint64) int {
	f.mu.Lock()
	delete(f.handles, fh)
	f.mu.Unlock()
	return 0
}

// Read reads from an open file.
func (f *FS) Read(_ string, buff []byte, ofst int64, fh uint64) int {
	f.mu.Lock()
	of := f.handles[fh]
	f.mu.Unlock()
	if of == nil {
		return -fuse.EBADF
	}
	if ofst < 0 {
		return -fuse.EINVAL
	}

	n, err := f.readAt(of, buff, uint64(ofst))
	if err != nil {
		debug.Log("reading %v at %d failed: %v", of.node.Name, ofst, err)
		return -fuse.EIO
	}
	return n
}

// prepare looks up the sizes of the blobs of an open file. This is deferred
// until the first read, because Windows opens many files only to query their
// attributes.
func (f *FS) prepare(of *openFile) error {
	of.once.Do(func() {
		cumsize := make([]uint64, 1+len(of.node.Content))
		for i, id := range of.node.Content {
			size, found := f.repo.LookupBlobSize(restic.DataBlob, id)
			if !found {
				of.err = errors.Errorf("id %v not found in repository", id)
				return
			}
			cumsize[i+1] = cumsize[i] + uint64(size)
		}
		of.cumsize = cumsize
	})
	return of.err
}

func (f *FS) readAt(of *openFile, buff []byte, offset uint64) (int, error) {
	if of.node.Type != data.NodeTypeFile {
		return 0, nil
	}
	if err := f.prepare(of); err != nil {
		return 0, err
	}

	size := of.cumsize[len(of.cumsize)-1]
	if offset >= size || len(buff) == 0 {
		return 0, nil
	}

	// skip the blobs before the offset
	start := sort.Search(len(of.cumsize), func(i int) bool {
		return of.cumsize[i] > offset
	}) - 1
	offset -= of.cumsize[start]

	read := 0
	for i := start; read < len(buff) && i < len(of.node.Content); i++ {
		id := of.node.Content[i]
		blob, err := f.blobCache.GetOrCompute(id, func() ([]byte, error) {
			return f.repo.LoadBlob(f.ctx, restic.DataBlob, id, nil)
		})
		if err != nil {
			return read, err
		}

		if offset > 0 {
			if offset >= uint64(len(blob)) {
				return read, errors.Errorf("blob %v is shorter than recorded in the index", id)
			}
			blob = blob[offset:]
			offset = 0
		}
		read += copy(buff[read:], blob)
	}
	return read, nil
}

// The remaining operations would modify the volume. All of them are rejected.

func (f *FS) Mknod(_ string, _ uint32, _ uint64) int    { return -fuse.EROFS }
func (f *FS) Mkdir(_ string, _ uint32) int              { return -fuse.EROFS }
func (f *FS) Unlink(_ string) int                       { return -fuse.EROFS }
func (f *FS) Rmdir(_ string) int                        { return -fuse.EROFS }
func (f *FS) Link(_ string, _ string) int               { return -fuse.EROFS }
func (f *FS) Symlink(_ string, _ string) int            { return -fuse.EROFS }
func (f *FS) Rename(_ string, _ string) int             { return -fuse.EROFS }
func (f *FS) Chmod(_ string, _ uint32) int              { return -fuse.EROFS }
func (f *FS) Chown(_ string, _ uint32, _ uint32) int    { return -fuse.EROFS }
func (f *FS) Utimens(_ string, _ []fuse.Timespec) int   { return -fuse.EROFS }
func (f *FS) Truncate(_ string, _ int64, _ uint64) int  { return -fuse.EROFS }
func (f *FS) Setxattr(_, _ string, _ []byte, _ int) int { return -fuse.EROFS }
func (f *FS) Removexattr(_ string, _ string) int        { return -fuse.EROFS }
func (f *FS) Write(_ string, _ []byte, _ int64, _ uint64) int {
	return -fuse.EROFS
}
func (f *FS) Create(_ string, _ int, _ uint32) (int, uint64) {
	return -fuse.EROFS, noHandle
}

// Chflags, Setcrtime and Setchgtime are optional operations of the host. They
// are implemented so that changing the attributes or the creation time of a
// file fails, instead of being silently ignored.

func (f *FS) Chflags(_ string, _ uint32) int           { return -fuse.EROFS }
func (f *FS) Setcrtime(_ string, _ fuse.Timespec) int  { return -fuse.EROFS }
func (f *FS) Setchgtime(_ string, _ fuse.Timespec) int { return -fuse.EROFS }

// Serve mounts the file system at mountpoint and blocks until the volume is
// unmounted or ctx is cancelled.
func (f *FS) Serve(ctx context.Context, mountpoint string) (err error) {
	host := fuse.NewFileSystemHost(f)
	host.SetCapCaseInsensitive(true)
	host.SetCapReaddirPlus(true)
	host.SetCapDeleteAccess(true)

	opts := []string{
		// all files belong to the user who mounts the volume
		"-o", "uid=-1",
		"-o", "gid=-1",
		"-o", "fstypename=restic",
		"-o", "volname=" + volumeLabel(f.cfg.VolumeName),
		// the contents of a snapshot never change, so Windows may cache them
		"-o", "FileInfoTimeout=-1",
		// the list of snapshots does change, so directory listings expire
		"-o", "DirInfoTimeout=5000",
	}

	done := make(chan struct{})
	defer close(done)
	go func() {
		select {
		case <-ctx.Done():
		case <-done:
			return
		}
		// the host can only be unmounted after it has been initialized
		select {
		case <-f.ready:
		case <-done:
			return
		}
		host.Unmount()
	}()

	defer func() {
		// the host panics if WinFsp is not installed
		if r := recover(); r != nil {
			err = errors.Fatalf("unable to mount: %v (is WinFsp installed?)", r)
		}
	}()

	if !host.Mount(mountpoint, opts) {
		return errors.Fatalf("unable to mount the repository at %s", mountpoint)
	}
	return nil
}

// volumeLabel makes name usable as a volume label and as a mount option.
func volumeLabel(name string) string {
	name = strings.Map(func(r rune) rune {
		if r < 0x20 || strings.ContainsRune(`,<>:"/\|?*`, r) {
			return '_'
		}
		return r
	}, strings.TrimSpace(name))
	if runes := []rune(name); len(runes) > maxVolumeNameLength {
		name = string(runes[:maxVolumeNameLength])
	}
	if name == "" {
		return DefaultVolumeName
	}
	return name
}

// CheckMountpoint reports whether mountpoint can be used. WinFsp accepts an
// unused drive letter such as "Z:", "*" for the next free drive letter, or the
// path of a directory that does not exist yet.
func CheckMountpoint(mountpoint string) error {
	if mountpoint == "*" {
		return nil
	}

	if isDriveLetter(mountpoint) {
		device, err := windows.UTF16PtrFromString(mountpoint)
		if err != nil {
			return errors.Fatalf("invalid mountpoint %s", mountpoint)
		}
		var target [windows.MAX_PATH]uint16
		if n, _ := windows.QueryDosDevice(device, &target[0], uint32(len(target))); n > 0 {
			return errors.Fatalf("drive %s is already in use", strings.ToUpper(mountpoint))
		}
		return nil
	}

	abs, err := filepath.Abs(mountpoint)
	if err != nil {
		return errors.Fatalf("invalid mountpoint %s: %v", mountpoint, err)
	}
	if _, err := os.Lstat(abs); err == nil {
		return errors.Fatalf("mountpoint %s already exists; use an unused drive letter such as Z: or a directory that does not exist yet", abs)
	}
	if fi, err := os.Stat(filepath.Dir(abs)); err != nil || !fi.IsDir() {
		return errors.Fatalf("parent directory of mountpoint %s does not exist", abs)
	}
	return nil
}

func isDriveLetter(s string) bool {
	if len(s) != 2 || s[1] != ':' {
		return false
	}
	c := s[0]
	return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
}
