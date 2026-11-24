# Storage status

The application shows the quota of the storage and how much of it is used, and warns
from 80 % on. The quota is a mark the administrator sets in the control centre and can
raise at any time, so that a sharp rise is noticed at once. It is not a booking and not a
limit: backups keep running beyond it; the figures inform, they do not block.

The figures come from two places. The application asks a service first, if the setup
code or the status file names one. If none is set up, or it cannot be reached, or it
answers with an error, the application reads the status file from the bucket instead.
If neither has figures, nothing is shown: so it is with an own Wasabi account whose
bucket holds no status file, and with a backup in a local folder.

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
| `bookedBytes` | The quota. The name stays from the time the storage was booked. 1 GB is 1024³ bytes. Must be greater than 0. |
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
it may count storage this computer cannot see. The quota always comes from the file.
The figures of the service are taken as they are.

A storage can hold several backups, each in a [destination of its own](storage-folders.md).
The quota is that of the whole bucket, so everything in it is counted, whichever
destinations the folders of this computer go to. A computer whose setup code limits it
to a folder of the bucket (field `p`, see [setup-code.md](setup-code.md)) can only list
that folder, so its own measurement counts that folder alone.

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
