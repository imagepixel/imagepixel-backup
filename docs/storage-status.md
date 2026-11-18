# Storage status

The application shows how much storage a customer has booked and how much of it is
used, and warns from 80 % on. Backups keep running when the storage is full; the
figures inform, they do not block.

The figures come from two places. The application asks the service of Imagepixel
first. If no service is set up, or it cannot be reached, or it answers with an error,
the application reads the status file from the bucket of the customer instead. If
neither has figures, nothing is shown. A backup in a local folder has no booked size.

## The document

The service and the status file use the same JSON document:

```json
{
  "version": 1,
  "bookedBytes": 107374182400,
  "usedBytes": 66571993088,
  "measuredAt": "2026-11-14T06:00:00Z",
  "apiUrl": "https://portal.example.test/api/v1",
  "apiToken": "..."
}
```

| Field | Meaning |
|---|---|
| `bookedBytes` | The booked size. 1 GB is 1024³ bytes. Must be greater than 0. |
| `usedBytes` | What the storage of the customer holds, servers and computers together. |
| `measuredAt` | When `usedBytes` was measured. |
| `apiUrl`, `apiToken` | Optional, only read from the status file: where the service is and which token to send. |

## The service

```
GET {apiUrl}/storage-status
Authorization: Bearer {apiToken}
Accept: application/json
```

The answer is the document above with status 200. Any other status, an answer that
is not the document, or no answer within 8 seconds makes the application read the
status file. The address must start with `https://`.

The application learns the address and the token from the setup code (fields `a` and
`t`) or from the status file. A status file that names a service makes every
application that reads it ask the service first from then on. This is how the service
can go live without setting up the applications again.

## The status file

The file is the object `imagepixel/storage-status.json` in the bucket of the customer.
The application reads it with the access key it backs up with. Whoever writes the
figures for the service also writes the file, so that both agree.

The file is only as fresh as whoever wrote it last. After reading it, the application
therefore lists the bucket and adds up what lies in it, without the objects below
`imagepixel/`. If that is more than the file says, the application shows its own
measurement with the time it was made; otherwise the figure of the file stays, because
it may count storage this computer cannot see. The booked size always comes from the
file. The figures of the service are taken as they are.

A storage can hold several backups, each in a [folder of its own](storage-folders.md).
The booked size is that of the whole bucket, so everything in it is counted, whichever
folder this computer backs up to.

The access key of a customer should be allowed to read the file but not to change it.
A statement like this in the policy of the customer does that:

```json
{
  "Effect": "Deny",
  "Action": ["s3:PutObject", "s3:DeleteObject"],
  "Resource": "arn:aws:s3:::BUCKET/imagepixel/*"
}
```

Until the service exists, `ipb-admin write-status` measures the bucket and writes the
file. It counts every object except those below `imagepixel/`.

```powershell
dotnet run --project tools/ImagepixelBackup.Admin -- write-status --bucket BUCKET --credentials keys.csv --booked-gb 100
```
