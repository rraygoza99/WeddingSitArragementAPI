# Wedding Seating Arrangement API

An ASP.NET Core Minimal API (.NET 8) designed to manage wedding seating arrangement JSON files stored in AWS S3. Built with AWS Lambda hosting support and owner-based access control using S3 object metadata and object tags (`X-OwnerEmail`).

---

## Features

- **AWS S3 Cloud Storage**: Stores and retrieves JSON seating charts from S3.
- **Owner-Based Authorization**: Every file is linked to one or multiple owner emails (`X-OwnerEmail`) in S3 User Metadata or Object Tags.
- **Multi-Owner Support**: A file can have multiple owners (e.g., both partners, wedding planners), separated by commas, semicolons, or pipes.
- **Metadata & Tag Fallback**: Inspects S3 User-Defined Metadata as well as AWS S3 Object Tags.
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

## Owner Identification & Access Control (`X-OwnerEmail`)

All file operations enforce owner verification. The requester's email can be passed using any of the following methods (in priority order):

1. **HTTP Header**: `X-OwnerEmail: partner1@gmail.com`
2. **Query Parameter**: `?ownerEmail=partner1@gmail.com`
3. **Request Body** (on `POST /api/weddings`): `"ownerEmail": "partner1@gmail.com, partner2@gmail.com"`
4. **Multipart Form Field** (on `POST /api/s3/upload`): `ownerEmail` or `X-OwnerEmail`

### Multi-Owner Support
Files can store single or multiple emails in `X-OwnerEmail`:
- Example: `partner1@gmail.com, partner2@gmail.com`
- Supported separators: `,`, `;`, `|`, or spaces.
- As long as the requester's email matches **any** email registered in the file's metadata/tags, access is permitted.

---

## API Endpoints

### 1. Create a Wedding
Creates a template wedding seating JSON file in S3 with metadata.

- **Endpoint**: `POST /api/weddings`
- **Headers**: `X-OwnerEmail: partner1@gmail.com` *(or pass in body/query)*
- **Request Body**:
  ```json
  {
    "partner1Name": "Alex",
    "partner2Name": "Sam",
    "weddingDate": "2026-09-20",
    "venue": "Grand Hall",
    "ownerEmail": "alex@gmail.com, sam@gmail.com"
  }
  ```
- **Response** (`201 Created`):
  ```json
  {
    "fileName": "Alex-Sam-2026-09-20.json",
    "weddingId": "b1b1c67d-9279-4d32-9cb7-28d84a7e9375",
    "ownerEmail": "alex@gmail.com, sam@gmail.com"
  }
  ```

---

### 2. List Wedding Files
Retrieves all JSON wedding files where the requester's email is registered as an owner.

- **Endpoint**: `GET /api/s3/list`
- **Headers**: `X-OwnerEmail: alex@gmail.com` *(or `?ownerEmail=alex@gmail.com`)*
- **Response** (`200 OK`):
  ```json
  [
    {
      "fileName": "Alex-Sam-2026-09-20.json",
      "size": 348,
      "lastModified": "2026-10-14T10:30:00Z",
      "ownerEmails": [
        "alex@gmail.com",
        "sam@gmail.com"
      ]
    }
  ]
  ```

---

### 3. Get / View Wedding File
Retrieves the raw JSON content of the wedding file.

- **Endpoint**: `GET /api/s3/file/{fileName}`
- **Headers**: `X-OwnerEmail: alex@gmail.com`
- **Response**: `200 OK` (Content-Type: `application/json`) / `403 Forbidden` if requester is not in the owners list.

---

### 4. Download Wedding File
Downloads the wedding JSON file as an attachment.

- **Endpoint**: `GET /api/s3/download/{fileName}`
- **Headers**: `X-OwnerEmail: alex@gmail.com`
- **Response**: `200 OK` (File stream) / `403 Forbidden` if requester is not in the owners list.

---

### 5. Upload Existing JSON File
Uploads an existing `.json` file to S3 with owner metadata. If the file already exists, only an authorized owner can overwrite it.

- **Endpoint**: `POST /api/s3/upload`
- **Content-Type**: `multipart/form-data`
- **Form Data**:
  - `file`: Selected `.json` file
  - `ownerEmail`: `alex@gmail.com, sam@gmail.com` (or via `X-OwnerEmail` header)
- **Response** (`200 OK`):
  ```json
  {
    "message": "Uploaded Alex-Sam-2026-09-20.json to S3.",
    "fileName": "Alex-Sam-2026-09-20.json",
    "ownerEmail": "alex@gmail.com, sam@gmail.com"
  }
  ```

---

### 6. Save / Update Wedding File Content
Overwrites or saves updated seating chart JSON data. Validates ownership before saving.

- **Endpoint**: `PUT /api/s3/file/{fileName}`
- **Headers**:
  - `X-OwnerEmail: alex@gmail.com`
  - `Content-Type: application/json`
- **Request Body**: Raw JSON payload of the seating arrangement.
- **Response** (`200 OK`):
  ```json
  {
    "message": "Saved Alex-Sam-2026-09-20.json to S3.",
    "fileName": "Alex-Sam-2026-09-20.json",
    "ownerEmail": "alex@gmail.com"
  }
  ```

---

### 7. Delete Wedding File
Deletes a wedding file from S3 (restricted to registered owners).

- **Endpoint**: `DELETE /api/s3/delete/{fileName}`
- **Headers**: `X-OwnerEmail: alex@gmail.com`
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
├── README.md                   # Project documentation
└── Properties/
    └── launchSettings.json     # Local development launch profiles
```
