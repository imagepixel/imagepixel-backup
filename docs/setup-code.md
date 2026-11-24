# Connecting to the online storage

The application backs up to a bucket at Wasabi, or to a folder such as a USB drive. For
the bucket it needs its name and region and an access key with its secret. The setup
offers two ways to them ("Einrichtungscode" and "Eigenes Wasabi-Konto"), when backing up
and when restoring.

## The setup code

The administrator who looks after the storage hands out a setup code, a single line
such as `IPB1-eyJiIjoi…`. In the control centre of Imagepixel the code is made when a
computer is added and shown once. Pasted into the setup, it fills in everything; the
customer only chooses the encryption password, which nobody else learns.

The code is `IPB1-` followed by a JSON object in Base64 for addresses (`-` and `_`
instead of `+` and `/`), without `=` at the end:

| Field | Meaning |
|---|---|
| `b` | The bucket. Needed. |
| `k`, `s` | The access key and its secret. Needed. |
| `r` | The region, such as `eu-central-2`. Without it, `eu-central-2` applies. |
| `p` | Optional: the folder of the bucket the access key is limited to, such as `geraete/<user>/`. Every backup made with the code lies inside it. |
| `n` | Optional: the name of the computer the code was made for. The application shows it next to the bucket. |
| `a`, `t` | Optional: address and token of a service for the [storage status](storage-status.md). |

Fields the application does not know are left aside. A code without `p` reaches the
whole bucket, and the customer chooses a [destination](storage-folders.md) in it; that is
how codes were made before `p` existed. A code with `p` needs version 0.2.0 or later:
an older one would read past the folder and then be turned down by the storage.

The folder in `p` is taken as it comes; the application does not assume any layout. It
follows the rules of a [destination](storage-folders.md#names) and must not begin with a
name the storage itself uses at its top (`imagepixel`, `config`, `data`, `index`, `keys`,
`locks`, `snapshots`), or the code is turned down. With `p`, the key needs to:

- read, write and delete (delete markers are enough) inside the folder,
- list the bucket with the prefix of the folder,
- read `imagepixel/storage-status.json`, if the overview is to show the quota.

The search for existing backups, the deletion of old destinations, the encrypted view
and the measuring of the storage then stay inside the folder as well.

`ipb-admin setup-code` makes codes by hand, with `--path` for the folder and `--name`
for the computer.

The code contains the access key in readable form. It is shown as dots in the setup and
kept, like the other secrets, protected for the user of the computer (the Data
Protection API on Windows, the keychain on macOS, the keyring of the desktop on Linux).

## An own Wasabi account

Without an administrator, the access data are typed in from the console of Wasabi: the
bucket, its region, the access key and its secret. A key just for this computer is the
safest. Nothing goes through Imagepixel; the storage status is not shown unless the
bucket holds a status file.

## A locked access

When the storage turns the access data down, for example because the administrator has
locked the key of this computer, backups fail with a sentence that says so, and the
overview offers "Einrichtungscode oder Speicher ändern" to enter a new code or new
access data. The folders and their settings stay.
