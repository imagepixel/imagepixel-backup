# Destinations in a storage

Every folder that is backed up has a destination: a folder inside the storage that its
backups go to. Each destination is a restic repository of its own, with its own saved
states, opened with the one encryption password of the application. With a destination
each, several computers can back up to one storage without their backups getting mixed
up, and the folders of one computer can be kept apart as well.

The setup asks for a folder below the setup code ("Ordner im Speicher"). It is the
destination of the folders chosen there, and of folders added later, until a folder is
given one of its own. Leaving it empty keeps the backups directly in the bucket, which is
where every backup made before destinations existed lies. The same holds for a backup in
a folder on a disk: a destination is a folder inside the folder of the backup.

In the overview each folder names its destination and has a button "Ziel …" to change it.

## What a bucket looks like

```
BUCKET/
  imagepixel/storage-status.json    kept by Imagepixel, see storage-status.md
  config, data/, index/, keys/,     a backup without a folder
  locks/, snapshots/
  Notebook/config, Notebook/data/…  a backup in the folder "Notebook"
  Büro/PC/config, …                 a backup in a folder inside a folder
```

The booked size is that of the whole bucket. The figures the application shows count
every backup in it, whichever destination the folders of this computer use.

## Names

A destination is one or more names separated by `/`. A name consists of letters and
digits of any language, spaces, `.`, `-` and `_`; it is not `.` or `..`. The whole
destination has at most 100 characters. What is typed is tidied up first: a backslash
counts as a slash, slashes and spaces at the ends are dropped.

The first name must not be one the storage itself uses at its top, in any spelling:
`imagepixel`, `config`, `data`, `index`, `keys`, `locks`, `snapshots`. A folder of such a
name would get mixed up with the files of Imagepixel or with a backup that lies directly
in the bucket.

## Finding the backups of a storage

Once the storage is known, the application looks into it and offers the backups it holds
for one click, in the setup and on the page of a destination, so that nobody has to
remember how a folder was spelled. This matters most when data is restored on a new
computer. It asks the storage for the folders at its top and takes those that hold the
file `config`, which every backup has; a backup directly in the bucket is found the same
way. On a disk it looks at the folders inside the folder of the backup. A backup in a
folder inside a folder is not looked for; its destination has to be typed.

## Changing the destination of a folder

The page "Ziel für …" shows where the backups of the folder go and lets the customer
type or pick another destination. Nothing changes until they save, and not while the
folder is being backed up.

- A destination that holds no backup yet is created with the password of the
  application. One that holds a backup the password does not open is refused, and
  nothing changes.
- From then on the folder is backed up there. Nothing is moved: the backups at the
  previous destination stay where they are, can still be restored and keep taking up
  storage. The folder counts as not backed up yet and, unless it is only backed up on
  demand, is backed up to the new destination right away. Schedule and exceptions stay.
- When the destination changes, the page asks what becomes of the backups of this
  folder at the previous one: keep them, or delete them after 7, 30 or 90 days. Nothing
  is chosen for the customer. The overview shows a waiting deletion below the folder,
  with a button to keep the backups after all.

## Deleting old backups

A waiting deletion is carried out when the application runs after its day has come,
between backups, never during one. In the destination it deletes the saved states of
that one folder and then the data that only they used (`restic forget` and
`restic prune`). Backups of other folders there are not touched. A deletion that fails is
tried again an hour later.

If that leaves the destination without any saved state, no folder goes there and it is
not the destination the storage was set up with, the repository is removed altogether:
the object `config` and everything below `keys/`, `data/`, `index/`, `snapshots/` and
`locks/` of that destination, and nothing else. Other folders inside it, files of other
programs and the files of Imagepixel stay, even at the top of a bucket.

A deletion is dropped without deleting anything when the folder goes back to that
destination before it is due, or when the customer moves to another storage: a
destination of the same name there holds other backups.

In a bucket with versioning and object lock, which Imagepixel sets up for its customers,
a deleted object stays as an older version until its retention period has passed, and
only then frees the storage. The figures of the application count what can still be
seen, so they drop at once.

## Restoring and the drive

The restore page lists the saved states of every destination that may hold backups of
this computer: the one of the storage, those of the folders, and those whose backups
wait to be deleted. With more than one, each line names its destination, and a state is
restored from where it lies. The drive shows one destination at a time; with more than
one, the overview lets the customer choose which.

## The encrypted view

"Verschlüsselt öffnen" next to "Laufwerk öffnen" shows the same destination the way it
lies in the storage: the files of the restic repository, encrypted, with checksums as
names. It is there so that customers can see for themselves that nothing in the storage
can be read without their password.

- For Imagepixel Storage, rclone shows the destination read-only as a drive (Windows,
  through WinFsp) or at a folder of the user (Linux, through FUSE). rclone is shipped with
  the application as released by its makers (see THIRD-PARTY-NOTICES.txt). It gets the
  access data through its environment, never on its command line, and reads and writes
  no settings file. Every read goes to the storage; nothing is kept on the computer. At
  the top of a bucket the folder `imagepixel/` is left out, because the figures in it are
  not part of the backup.
- For a backup in a folder on a disk, the file manager opens that folder: the files lie
  there as they are.
- macOS has no drive, so the view is not offered there.

The drive closes like the other one, and when the application ends. A view an
application left open when it did not end properly is closed at the next start.
