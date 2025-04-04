# netRSS

A .NET 9.0 RSS reader application.

## Running Locally

To run the application locally in development mode:

```bash
cd app
dotnet run
```

The application will be available at the default URL.

## Docker Setup

This project includes Docker configuration for easy development and deployment.

### Prerequisites

- Docker
- Docker Compose

### Running with Docker

1. Clone this repository
2. Run the application:

```bash
docker compose up
```

The application will be available at http://localhost:15001

### Database Configuration

By default, the application will create and use an internal SQLite database. If you want to use an existing database file:

1. Place your `rss.db` file in a `./database` directory at the root of the project
2. Uncomment the volume mapping line in the `docker-compose.yml` file:

```yaml
# Uncomment the next line if ./database/rss.db exists on your host machine
- ./database/rss.db:/app/app/Data/rss.db
```

## Development

This Docker setup is configured for development mode and includes:

- Hot reload with `dotnet watch`
- Development environment settings
- Persistent ASP.NET Core Data Protection keys
