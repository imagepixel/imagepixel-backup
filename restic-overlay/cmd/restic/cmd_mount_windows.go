//go:build windows

package main

import (
	"context"

	"github.com/spf13/cobra"
	"github.com/spf13/pflag"

	"github.com/restic/restic/internal/data"
	"github.com/restic/restic/internal/debug"
	"github.com/restic/restic/internal/errors"
	"github.com/restic/restic/internal/global"
	"github.com/restic/restic/internal/ui"
	"github.com/restic/restic/internal/winmount"
)

func registerMountCommand(cmdRoot *cobra.Command, globalOptions *global.Options) {
	cmdRoot.AddCommand(newMountCommand(globalOptions))
}

func newMountCommand(globalOptions *global.Options) *cobra.Command {
	var opts MountOptions

	cmd := &cobra.Command{
		Use:   "mount [flags] mountpoint",
		Short: "Mount the repository",
		Long: `
The "mount" command mounts the repository read-only as a Windows volume at the
given mountpoint. This requires WinFsp (https://winfsp.dev) to be installed.

The mountpoint is an unused drive letter such as "Z:", "*" to use the next free
drive letter, or the path of a directory that does not exist yet.

The volume contains one directory per snapshot, named after the time of the
snapshot. The format of the names can be changed with --time-template. You need
to specify a sample format for exactly the following timestamp:

    Mon Jan 2 15:04:05 -0700 MST 2006

The volume is read-only: files can be opened and copied, but nothing in the
repository can be changed, renamed or deleted through it.

EXIT STATUS
===========

Exit status is 0 if the command was successful.
Exit status is 1 if there was any error.
Exit status is 10 if the repository does not exist.
Exit status is 11 if the repository is already locked.
Exit status is 12 if the password is incorrect.
`,
		DisableAutoGenTag: true,
		GroupID:           cmdGroupDefault,
		RunE: func(cmd *cobra.Command, args []string) error {
			finalizeSnapshotFilter(&opts.SnapshotFilter)
			return runMount(cmd.Context(), opts, *globalOptions, args, globalOptions.Term)
		},
	}

	opts.AddFlags(cmd.Flags())
	return cmd
}

// MountOptions collects all options for the mount command.
type MountOptions struct {
	data.SnapshotFilter
	TimeTemplate string
	VolumeName   string
}

func (opts *MountOptions) AddFlags(f *pflag.FlagSet) {
	initMultiSnapshotFilter(f, &opts.SnapshotFilter, true)

	f.StringVar(&opts.TimeTemplate, "time-template", winmount.DefaultTimeTemplate, "set `template` to use for the names of snapshot directories")
	f.StringVar(&opts.VolumeName, "volume-name", winmount.DefaultVolumeName, "set the `name` of the volume as shown by Windows")
}

func runMount(ctx context.Context, opts MountOptions, gopts global.Options, args []string, term ui.Terminal) error {
	printer := ui.NewProgressPrinter(false, gopts.Verbosity, term)

	if opts.TimeTemplate == "" {
		return errors.Fatal("time template string cannot be empty")
	}
	if len(args) != 1 {
		return errors.Fatal("wrong number of parameters")
	}

	mountpoint := args[0]
	if err := winmount.CheckMountpoint(mountpoint); err != nil {
		return err
	}

	debug.Log("start mount")
	defer debug.Log("finish mount")

	ctx, repo, unlock, err := openWithReadLock(ctx, gopts, gopts.NoLock, printer)
	if err != nil {
		return err
	}
	defer unlock()

	if err := repo.LoadIndex(ctx, printer); err != nil {
		return err
	}

	printer.S("Loading snapshots...")
	fsys, err := winmount.New(ctx, repo, winmount.Config{
		Filter:       opts.SnapshotFilter,
		TimeTemplate: opts.TimeTemplate,
		VolumeName:   opts.VolumeName,
		Ready: func() {
			printer.S("Now serving the repository at %s", mountpoint)
			printer.S("When finished, quit with Ctrl-c here.")
		},
	})
	if err != nil {
		return err
	}

	debug.Log("serving mount at %v", mountpoint)
	if err := fsys.Serve(ctx, mountpoint); err != nil {
		return err
	}

	if ctx.Err() != nil {
		// interrupted by the user, this is the regular way to unmount
		return ErrOK
	}
	return nil
}
