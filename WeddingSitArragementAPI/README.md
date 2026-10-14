# Wedding Seating Arrangement API

An ASP.NET Core Minimal API (.NET 8) designed to manage wedding seating arrangement JSON files stored in AWS S3. Built with AWS Lambda hosting support and owner-based access control using S3 object metadata (`X-OwnerEmail`).

---

## Features

- **AWS S3 Cloud Storage**: Stores and retrieves JSON seating charts from S3.
- **Owner-Based Authorization**: Every file is tagged with `X-OwnerEmail` metadata. Only the owner can list, view, download, update, or delete their files.
- **Serverless Ready**: Configured with `Amazon.Lambda.AspNetCoreServer.Hosting` for deployment as an AWS Lambda function behind HTTP API Gateway / ALB.
- **Swagger / OpenAPI**: Interactive API documentation available at `/swagger`.

---

## Tech Stack

- **Framework**: .NET 8 (C#)
- **Architecture**: Minimal APIs
- **AWS SDK**: AWSSDK.S3, AWSSDK.Extensions.NETCore.Setup
- **Serverless**: Amazon.Lambda.AspNetCoreServer.Hosting
- **Documentation**: Swashbuckle (Swagger UI)

---

## Configuration

### AWS Credentials & Region

AWS configuration can be defined in [appsettings.json](appsettings.json), .NET User Secrets, or Environment Variables:

```json
{
  "AWS": {
    "Region": "us-east-2",
    "AccessKey": "YOUR_AWS_ACCESS_KEY",
    "SecretKey": "YOUR_AWS_SECRET_KEY"
  }
}
```

> **Note**: For local development, using **User Secrets** or the default AWS credentials profile (`~/.aws/credentials`) is recommended over hardcoding credentials in configuration files.

To set User Secrets:
```bash
dotnet user-secrets set "AWS:AccessKey" "YOUR_ACCESS_KEY"
dotnet user-secrets set "AWS:SecretKey" "YOUR_SECRET_KEY"
dotnet user-secrets set "AWS:Region" "us-east-2"
```

---

## Owner Identification (`X-OwnerEmail`)

All file operations enforce owner verification. The owner's email address can be passed using any of the following methods (in priority order):

1. **HTTP Header**: `X-OwnerEmail: brideandgroom@example.com`
2. **Query Parameter**: `?ownerEmail=brideandgroom@example.com`
3. **Request Body** (on `POST /api/weddings`): `"ownerEmail": "brideandgroom@example.com"`
4. **Multipart Form Field** (on `POST /api/s3/upload`): `ownerEmail` or `X-OwnerEmail`

---

## API Endpoints

### 1. Create a Wedding
Creates a template wedding seating JSON file in S3 with metadata.

- **Endpoint**: `POST /api/weddings`
- **Headers**: `X-OwnerEmail: user@example.com` (or pass in body/query)
- **Request Body**:
  ```json
  {
    "partner1Name": "Alex",
    "partner2Name": "Sam",
    "weddingDate": "2026-09-20",
    "venue": "Grand Hall",
    "ownerEmail": "user@example.com"
  }
  ```
- **Response** (`201 Created`):
  ```json
  {
    "fileName": "Alex-Sam-2026-09-20.json",
    "weddingId": "b1b1c67d-9279-4d32-9cb7-28d84a7e9375",
    "ownerEmail": "user@example.com"
  }
  ```

---

### 2. List Wedding Files
Retrieves all JSON wedding files owned by the specified email.

- **Endpoint**: `GET /api/s3/list`
- **Headers**: `X-OwnerEmail: user@example.com` *(or `?ownerEmail=user@example.com`)*
- **Response** (`200 OK`):
  ```json
  [
    {
      "fileName": "Alex-Sam-2026-09-20.json",
      "size": 348,
      "lastModified": "2026-10-14T10:30:00Z",
      "ownerEmail": "user@example.com"
    }
  ]
  ```

---

### 3. Get / View Wedding File
Retrieves the raw JSON content of the wedding file.

- **Endpoint**: `GET /api/s3/file/{fileName}`
- **Headers**: `X-OwnerEmail: user@example.com`
- **Response**: `200 OK` (Content-Type: `application/json`) / `403 Forbidden` if not owner.

---

### 4. Download Wedding File
Downloads the wedding JSON file as an attachment.

- **Endpoint**: `GET /api/s3/download/{fileName}`
- **Headers**: `X-OwnerEmail: user@example.com`
- **Response**: `200 OK` (File stream) / `403 Forbidden` if not owner.

---

### 5. Upload Existing JSON File
Uploads an existing `.json` file to S3.

- **Endpoint**: `POST /api/s3/upload`
- **Content-Type**: `multipart/form-data`
- **Form Data**:
  - `file`: Selected `.json` file
  - `ownerEmail`: `user@example.com` (or via `X-OwnerEmail` header)
- **Response** (`200 OK`):
  ```json
  {
    "message": "Uploaded Alex-Sam-2026-09-20.json to S3.",
    "fileName": "Alex-Sam-2026-09-20.json",
    "ownerEmail": "user@example.com"
  }
  ```

---

### 6. Save / Update Wedding File Content
Overwrites or saves updated seating chart JSON data.

- **Endpoint**: `PUT /api/s3/file/{fileName}`
- **Headers**:
  - `X-OwnerEmail: user@example.com`
  - `Content-Type: application/json`
- **Request Body**: Raw JSON payload of the seating arrangement.
- **Response** (`200 OK`):
  ```json
  {
    "message": "Saved Alex-Sam-2026-09-20.json to S3.",
    "fileName": "Alex-Sam-2026-09-20.json",
    "ownerEmail": "user@example.com"
  }
  ```

---

### 7. Delete Wedding File
Deletes a wedding file from S3 (restricted to file owner).

- **Endpoint**: `DELETE /api/s3/delete/{fileName}`
- **Headers**: `X-OwnerEmail: user@example.com`
- **Response** (`200 OK`):
  ```
  Deleted Alex-Sam-2026-09-20.json from S3.
  ```

---

## Running Locally

1. **Restore dependencies**:
   ```bash
   dotnet restore
   ```

2. **Build the project**:
   ```bash
   dotnet build
   ```

3. **Run the API**:
   ```bash
   dotnet run
   ```

4. **Access Swagger UI**:
   Navigate to `https://localhost:62952/swagger` or `http://localhost:62953/swagger` in your browser.

---

## Project Structure

```text
├── Program.cs                  # Main entry point, DI setup, and endpoint mappings
├── WeddingSitArragementAPI.csproj # Project file and NuGet dependencies
├── appsettings.json            # Base application configuration
└── Properties/
    └── launchSettings.json     # Local development launch profiles
```
