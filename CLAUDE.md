# Sports Venue — Backend API

## Project overview
ASP.NET Core Web API backend for the Sports Venue multi-sport public venue management platform.
Serves the React admin dashboard (`sports-venue-dashboard/`).
Primary market: **Jordan** — default currency JOD.

---

## Tech stack
| Layer | Technology |
|-------|-----------|
| Framework | ASP.NET Core 9 Web API |
| ORM | Entity Framework Core 9 |
| Database | MySQL (via Pomelo.EntityFrameworkCore.MySql) |
| Auth | JWT Bearer + httpOnly cookie for refresh |
| Password hashing | BCrypt.Net-Next |
| CORS | ASP.NET Core CORS middleware |
| Server | Kestrel (port 8000) |

---

## Project structure
```
sports-venue-api/
├── SportsVenueApi.sln              # Solution file (open in Visual Studio)
├── SportsVenueApi/
│   ├── Program.cs                  # App setup, DI, middleware pipeline
│   ├── appsettings.json            # Connection string, JWT config, CORS
│   ├── Properties/
│   │   └── launchSettings.json     # Visual Studio launch profile (port 8000)
│   ├── SportsVenueApi.csproj       # NuGet packages
│   ├── Models/                     # EF Core entities
│   │   ├── User.cs
│   │   ├── Venue.cs
│   │   ├── Booking.cs
│   │   └── Payment.cs
│   ├── Data/
│   │   ├── AppDbContext.cs         # EF Core DbContext
│   │   └── SeedData.cs             # Seed script — mirrors frontend mock data
│   ├── DTOs/                       # Request/response shapes
│   │   ├── ApiResponse.cs          # Standard envelope
│   │   ├── Auth/
│   │   ├── Users/
│   │   ├── Venues/
│   │   ├── Bookings/
│   │   ├── Payments/
│   │   └── Reports/
│   ├── Controllers/                # API endpoints
│   │   ├── AuthController.cs
│   │   ├── UsersController.cs
│   │   ├── VenuesController.cs
│   │   ├── BookingsController.cs
│   │   ├── PaymentsController.cs
│   │   └── ReportsController.cs
│   └── Services/
│       └── JwtService.cs           # JWT token creation/validation
└── CLAUDE.md
```

---

## Commands
```bash
# Restore packages
dotnet restore

# Build
dotnet build

# Seed database (drops + recreates + inserts data)
dotnet run -- --seed

# Run server (port 8000)
dotnet run

# Open in Visual Studio
# Open SportsVenueApi.sln — runs on port 8000 (configured in launchSettings.json)

# API docs
# http://localhost:8000/openapi/v1.json
```

---

## Configuration (appsettings.json)
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Port=3306;Database=sportsvenue;User=root;Password=password;"
  },
  "Jwt": {
    "SecretKey": "dev-secret-key-change-in-production",
    "AccessTokenExpireMinutes": "15",
    "RefreshTokenExpireDays": "7"
  },
  "Cors": {
    "Origins": "http://localhost:5173"
  }
}
```

---

## Auth flow
- Login → `POST /api/v1/auth/login` → returns `accessToken` in body + `refresh_token` in httpOnly cookie
- Access token: JWT (HS256), 15min expiry, sent as `Authorization: Bearer <token>`
- Refresh token: JWT, 7 days expiry, httpOnly cookie
- `POST /api/v1/auth/refresh` → reads cookie → returns new access token
- `POST /api/v1/auth/logout` → clears cookie
- Only `super_admin` and `venue_owner` can log in to the dashboard

---

## Dev credentials
| Role | Email | Password |
|------|-------|----------|
| super_admin | admin@sportsvenue.jo | admin123 |
| venue_owner | khalid@venues.jo | owner123 |

---

## API response envelope
All endpoints return:
```json
{
  "success": true,
  "data": {},
  "message": "string",
  "pagination": { "page": 1, "limit": 20, "total": 100 }
}
```

---

## Key endpoints
```
Auth
  POST   /api/v1/auth/login
  POST   /api/v1/auth/refresh
  POST   /api/v1/auth/logout

Venues
  GET    /api/v1/venues?page=&limit=&search=&sport=&status=&owner_id=
  POST   /api/v1/venues
  GET    /api/v1/venues/{id}
  PATCH  /api/v1/venues/{id}
  DELETE /api/v1/venues/{id}
  GET    /api/v1/venues/{id}/stats

Users (admin only)
  GET    /api/v1/users?page=&limit=&role=&search=
  PATCH  /api/v1/users/{id}/status
  PATCH  /api/v1/users/{id}/role
  PATCH  /api/v1/users/{id}/avatar

Bookings
  GET    /api/v1/bookings?page=&limit=&status=&venue_id=&from=&to=&owner_id=

Payments (admin only)
  GET    /api/v1/payments?page=&limit=&status=

Reports
  GET    /api/v1/reports/summary?owner_id=
  GET    /api/v1/reports/revenue-chart?days=30
  GET    /api/v1/reports/top-venues
  GET    /api/v1/reports/sports-breakdown
  GET    /api/v1/reports/export?format=csv&from=&to=&venue_id=
```

---

## Database models

### User
- `id` (string PK), `name`, `email` (unique), `phone`, `password_hash`, `role`, `status`, `avatar` (text), `created_at`

### Venue
- `id` (string PK), `name`, `owner_id` (FK users), `sports` (JSON), `city`, `address`, `price_per_hour`, `status`, `description` (text), `images` (JSON), `latitude`, `longitude`, `created_at`
- Navigation: `Owner` → User

### Booking
- `id` (string PK), `venue_id` (FK venues), `player_id` (FK users), `sport`, `date`, `duration`, `amount`, `status`, `created_at`
- Navigation: `Venue` → Venue, `Player` → User

### Payment
- `id` (string PK), `booking_id` (FK bookings), `player_id` (FK users), `amount`, `method`, `status`, `date`
- Navigation: `Booking` → Booking, `Player` → User

---

## Owner scoping
When `venue_owner` is logged in, the backend enforces `owner_id = current_user.id` on:
- `GET /venues` — only returns their venues
- `GET /bookings` — only bookings for their venues
- `GET /reports/summary` — only their stats

Enforced server-side regardless of what the frontend sends.

---

## Code conventions
- All controllers return `ApiResponse<T>` envelope
- Response field names use **camelCase** via `[JsonPropertyName]` attributes
- Database columns use **snake_case** via `[Column]` attributes
- `sports` and `images` on Venue stored as JSON strings with `[NotMapped]` List<string> accessors
- Sport filtering uses `LIKE` on the JSON string (contains the sport name in quotes)
- JWT claims: `sub` (user_id), `role`, `type` (access/refresh)
- ASP.NET auto-maps JWT `sub` → `ClaimTypes.NameIdentifier` and `role` → `ClaimTypes.Role` — always use `ClaimTypes.*` constants in controllers
- `[Authorize]` on all controllers except AuthController
- All queries with `.Include()` use `.AsSplitQuery()` to avoid MySQL sort buffer overflow on large JOINs
- Count queries are separated from data queries (don't include `.Include()` in the count)

---

## MySQL notes
- Requires MySQL 8.0+ (for JSON column support)
- If you get "Out of sort memory" errors: `SET GLOBAL sort_buffer_size = 4194304;` (4MB)
- The `.AsSplitQuery()` pattern on all Include queries prevents most sort buffer issues
- Database is created automatically by `EnsureCreated()` during seed — no migrations needed

---

## Seed data
- 15 users (DiceBear avatars, hashed passwords: admin123 / owner123 / password123)
- 8 venues (Jordan cities, real GPS coordinates, picsum images)
- 25 bookings (various statuses: pending, confirmed, completed, cancelled)
- 20 payments (various methods: Credit Card, Cliq, Bank Transfer)

Run: `dotnet run -- --seed` (drops + recreates all tables + inserts data)

---

## Connecting to frontend
In the frontend `.env`:
```env
VITE_MOCK_API=false
VITE_API_URL=http://localhost:8000/api/v1
```

### Running the full stack
```bash
# Terminal 1: Backend
cd sports-venue-api/SportsVenueApi
dotnet run                    # http://localhost:8000

# Terminal 2: Frontend
cd sports-venue-dashboard
npm run dev                   # http://localhost:5173
```

Or open `SportsVenueApi.sln` in Visual Studio and press F5 for the backend.
