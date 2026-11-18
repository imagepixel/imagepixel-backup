# Folders in a storage

A backup in Imagepixel Storage lies either directly in the bucket of the customer or in
a folder inside it. With a folder each, several computers can back up to one storage
without their backups getting mixed up: every folder is a backup of its own, with its
own saved states, opened with the password it was set up with.

The folder is optional. The setup asks for it below the setup code, and the overview
names it next to the storage. A backup made before folders existed lies directly in the
bucket and stays there; the application keeps using it unless a folder is entered.

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
every backup in it, whichever folder the computer itself uses.

## Names

A folder is one or more names separated by `/`. A name consists of letters and digits of
any language, spaces, `.`, `-` and `_`; it is not `.` or `..`. The whole folder has at
most 100 characters. What is typed is tidied up first: a backslash counts as a slash,
slashes and spaces at the ends are dropped.

The first name must not be one the storage itself uses at its top, in any spelling:
`imagepixel`, `config`, `data`, `index`, `keys`, `locks`, `snapshots`. A folder of such a
name would get mixed up with the files of Imagepixel or with a backup that lies directly
in the bucket.

## Finding the backups of a storage

Once the storage is known, the application looks into it and offers the backups it holds
for one click, so that nobody has to remember how a folder was spelled. This matters
most when data is restored on a new computer. It asks the storage for the folders at its
top and takes those that hold the file `config`, which every backup has; a backup
directly in the bucket is found the same way. A backup in a folder inside a folder is not
looked for, its folder has to be typed.

## Moving to another folder

"Ziel ändern" in the overview leads to the setup again. For another folder of the storage
in use the setup code is not needed a second time, because the application holds the
access data: the code field stays empty, and the password of the backup is asked for.
A folder that holds no backup yet starts an empty one, after the password was typed
twice.

Nothing is moved by this. The backup in the folder that was used before stays where it
is, keeps taking up storage, and can be opened again by going back to its folder. The
folders that are backed up come along with their schedules and exceptions, and are
backed up to the new place right away.
