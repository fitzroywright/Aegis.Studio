# Studio Storage Targets

Aegis.Studio uses `Common.Storage.IFileStorage` for application-managed files. The selected target applies to:

- job and conversation attachments;
- Studio in-app message attachments;
- workspace gallery originals, previews, thumbnails and publication manifests;
- Media asset/version content.

The raw Media Archive remains a separate filesystem/archive target because it represents camera/archive verification rather than ordinary application object storage.

## Provider selection

Set `Storage:Provider` to one of:

- `LocalFolder`
- `NetworkFolder`
- `SharePoint`
- `AzureBlob`
- `AmazonS3`

The application is otherwise provider-neutral.

### LocalFolder

```json
"Storage": {
  "Provider": "LocalFolder",
  "Local": {
    "RootPath": "D:/AegisStudio"
  }
}
```

For development the repository default uses `.`, resolved from the Studio content root, so the existing `Storage/...` and `VersionStore/...` layout remains valid.

### NetworkFolder

Windows UNC example:

```json
"Storage": {
  "Provider": "NetworkFolder",
  "NetworkFolder": {
    "RootPath": "\\\\nas01\\Studio"
  }
}
```

Linux deployments may use a mounted SMB/NFS path such as `/mnt/studio`. The share must honor exclusive file-lock semantics used by Common.Storage.

### SharePoint

```json
"Storage": {
  "Provider": "SharePoint",
  "SharePoint": {
    "TenantId": "<tenant-id>",
    "ClientId": "<app-registration-client-id>",
    "ClientSecretName": "storage/sharepoint/client-secret",
    "HostName": "tenant.sharepoint.com",
    "SitePath": "sites/Bryans",
    "DriveName": "Documents",
    "RootFolder": "Aegis/Studio"
  }
}
```

Store the client secret through Common.Secrets using the configured `ClientSecretName`. Do not put the secret in appsettings.

`DriveId` may be used instead of `DriveName`.

## Switching targets

Changing `Storage:Provider` changes where new reads and writes are resolved after restart. It does **not** silently copy existing content between targets. Migrate the existing Common.Storage keys before switching a production instance. This deliberate behavior prevents partial fallback between two authoritative stores.

Feature code must not construct `LocalFileStorage`, `NetworkFolderStorage` or `SharePointFileStorage` directly. It receives `IFileStorage` from dependency injection.

## Logical layout

Studio preserves provider-neutral logical areas:

- `Storage/attachments`
- `Storage/attachments/messages`
- `Storage/galleries`
- `VersionStore`

The same logical keys are used regardless of selected provider.

## Health

The Studio readiness check calls `IFileStorage.CheckHealthAsync` against the selected target. SharePoint health therefore proves Graph authentication plus write/read/delete access to the configured document library rather than checking a local folder.

### Azure Blob

```json
"Storage": {
  "Provider": "AzureBlob",
  "AzureBlob": {
    "ConnectionStringSecretName": "storage/azureblob/connection-string",
    "ContainerName": "aegis-studio",
    "RootFolder": "Aegis/Studio"
  }
}
```

The connection string is resolved through Common.Secrets. The same logical storage keys are used as the other targets.

### Amazon S3

```json
"Storage": {
  "Provider": "AmazonS3",
  "AmazonS3": {
    "BucketName": "example-studio",
    "Region": "us-east-1",
    "AccessKeyId": "",
    "SecretAccessKeyName": "storage/amazons3/secret-access-key",
    "RootFolder": "Aegis/Studio",
    "ServiceUrl": ""
  }
}
```

When access-key settings are empty, the AWS SDK default credential chain may be used, including an IAM role. For S3-compatible endpoints, set `ServiceUrl` and use path-style addressing.
