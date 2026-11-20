# Pairing a computer with the customer portal

Besides the setup code, a computer can get its access data to Imagepixel Storage by
pairing with the customer portal of Imagepixel. The customer does not copy any key: the
application shows a code, the customer enters it in the portal together with a name for
the computer, and the access data arrive encrypted for this one computer. Each computer
gets a key of its own that only reaches its own folder in the bucket of the customer, so
it can be locked in the portal without touching the other computers.

The setup offers both ways when backing up ("Mit dem Kundenportal koppeln" and
"Einrichtungscode eingeben"); so does "Einrichtungscode oder Speicher ändern" on the info
page. Restoring takes the setup code: the folder of a newly paired computer holds no
backup yet.

## How it goes

1. The application makes a key pair (P-256) and a secret of 32 random bytes. Both are
   kept with the other secrets of the application: on Windows encrypted for the user
   with the Data Protection API, on macOS in the keychain.
2. It calls `kopplung_beginnen` with the public key, the SHA-256 of the secret and the
   system (`windows` or `macos`) and gets a code such as `K7QM-4TZP`, valid for 30
   minutes. The code has 8 characters from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`; case,
   spaces and hyphens do not matter.
3. The application shows the code and asks `kopplung_abholen` every few seconds, with the
   code and the secret. The answer is one of `wartet` (the code waits to be entered),
   `in-arbeit` (it was entered, the access data are being made), `fertig` (with the
   encrypted access data), `fehler` (with a sentence for the customer) and `abgelaufen`.
   After `fehler` and `abgelaufen` the application offers a new code.
4. With `fertig` the application decrypts the access data, keeps them with its secrets
   and calls `kopplung_bestaetigen`, after which the portal deletes its copy. If that
   call fails, it is tried again; the portal deletes the copy an hour after it was made
   anyway.
5. The customer chooses the encryption password as with a setup code. Once the backup is
   set up, the access data are kept like those of a setup code, and what the pairing
   kept is deleted.

A pairing that waits survives a restart of the application: the setup goes on with the
same code.

The functions are called as `POST <portal>/rest/v1/rpc/<name>` with a JSON object of
the parameters (`p_schluessel`, `p_geheimnis_hash`, `p_system`; `p_code`, `p_geheimnis`)
and the headers `apikey: <public key of the portal>` and `Content-Profile: portal`. Errors
come as an HTTP status with `{"message": "…"}`; for 400, 403 and 429 the message is shown
to the customer.

## The encryption

The sender makes a key pair of its own (P-256) for each message and

1. agrees on a secret with the public key of the computer: ECDH, the x coordinate, 32 bytes;
2. takes as information the UTF-8 of `imagepixel-backup-kopplung-v1|` followed by the code
   without hyphens in capitals, such as `imagepixel-backup-kopplung-v1|K7QM4TZP`;
3. derives the key with HKDF-SHA256 from the secret, 16 random bytes of salt and the
   information, 32 bytes;
4. encrypts with AES-256-GCM, 12 random bytes of nonce, the information as additional
   data, a tag of 16 bytes.

The message is `{"v": 1, "epk": …, "salt": …, "nonce": …, "ct": …}`: the public key of
the sender as SubjectPublicKeyInfo in DER form, and the encrypted text followed by the
tag, all in Base64. The application decrypts with what .NET brings
(`ECDiffieHellman.DeriveRawSecretAgreement`, `HKDF`, `AesGcm`); the tests check it
against messages made with the crypto module of Node.js, among them the test vector of
the portal side.

The decrypted access data use the short names of the setup code:

| Field | Meaning |
|---|---|
| `v` | 1 |
| `g`, `n` | The number of the computer in the portal and the name the customer gave it |
| `b`, `r` | Bucket and region |
| `k`, `s` | The access key of the computer and its secret |
| `p` | The folder of the bucket the key is limited to, taken as it comes |
| `a`, `t` | Optional: address and token of the service for the storage status |

Other fields are left aside.

## In the storage

Every repository of a paired computer lies below `p`: the destination of the setup at
its top, other destinations inside it. The search for existing backups, the deletion of
old destinations and the encrypted view stay inside it as well. A computer that moves
from a setup code to a pairing moves to another storage in the sense of the setup: its
folders are backed up anew in the folder of the computer, and the previous backups stay
where they are.

The storage status works as with a setup code: from the service named by `a` and `t` if
there is one, otherwise from the status file at the top of the bucket, which the key of
a computer may read as the one thing outside its folder. What the application measures
itself is only what lies in the folder of the computer, so the figure of the status file,
which counts the whole storage, stays unless it is smaller.

## Where the portal is

The address of the portal and its public key are not part of the application. It looks
for them in this order:

1. the environment variables `IMAGEPIXEL_BACKUP_PORTAL_URL` and `IMAGEPIXEL_BACKUP_PORTAL_KEY`;
2. a file `portal.json` in the folder of the settings (`%APPDATA%\ImagepixelBackup` on
   Windows, or the folder named by `IMAGEPIXEL_BACKUP_DATA`);
3. a file `portal.json` next to the program.

```json
{ "url": "https://portal.example.test", "apiKey": "…" }
```

Only an address starting with `https://` is taken. Without settings, or on Linux, which
the portal does not know as a system, the setup offers the setup code only.
`build-installer.ps1 -PortalSettings <file>` puts such a file next to the program in the
setup program for Windows.
