# YallaNhjez — Backend API

## Project overview
ASP.NET Core 9 Web API backend for the YallaNhjez multi-sport venue booking platform.
Serves the React admin dashboard and the Flutter mobile app.
Primary market: **Jordan** — default currency JOD.

---

## Tech stack
| Layer | Technology |
|-------|-----------|
| Framework | ASP.NET Core 9 Web API |
| ORM | Entity Framework Core 9 (with migrations) |
| Database | MySQL 8.0+ (via Pomelo.EntityFrameworkCore.MySql) |
| Auth | JWT Bearer (HS256) + httpOnly cookie for refresh |
| Password hashing | BCrypt.Net-Next |
| Push notifications | Firebase Admin SDK (FCM) |
| Logging | Serilog (structured logging) |
| File uploads | Multipart form, stored in wwwroot/uploads/ |
| CORS | ASP.NET Core CORS middleware |
| Server | Kestrel (port 8000) |

---

## Project structure
```
yalla-nhjez-api/
├── SportsVenueApi.sln
├── SportsVenueApi/
│   ├── Program.cs                  # App setup, DI, middleware, auto-migration
│   ├── appsettings.json            # Connection string, JWT, CORS, Firebase
│   ├── firebase-credentials.json   # Firebase service account (not committed)
│   ├── SportsVenueApi.csproj
│   ├── Models/
│   │   ├── User.cs
│   │   ├── Venue.cs
│   │   ├── Booking.cs
│   │   ├── Payment.cs
│   │   ├── Notification.cs
│   │   ├── NotificationTemplate.cs
│   │   ├── DeviceToken.cs
│   │   ├── Favorite.cs
│   │   ├── RecurringBookingGroup.cs
│   │   ├── LoyaltyProgram.cs
│   │   └── LoyaltyPoints.cs
│   ├── Data/
│   │   ├── AppDbContext.cs         # DbContext with all DbSets
│   │   └── SeedData.cs            # Seed script (15 users, 8 venues, etc.)
│   ├── DTOs/
│   │   ├── ApiResponse.cs         # Standard envelope + PaginationInfo
│   │   ├── Auth/
│   │   ├── Users/
│   │   ├── Venues/
│   │   ├── Bookings/
│   │   ├── Payments/
│   │   ├── Notifications/
│   │   ├── Loyalty/
│   │   └── Reports/
│   ├── Controllers/
│   │   ├── AuthController.cs
│   │   ├── UsersController.cs
│   │   ├── VenuesController.cs
│   │   ├── BookingsController.cs
│   │   ├── PaymentsController.cs
│   │   ├── ReportsController.cs
│   │   ├── NotificationsController.cs
│   │   ├── FavoritesController.cs
│   │   └── UploadsController.cs
│   ├── Services/
│   │   ├── JwtService.cs           # JWT token creation/validation
│   │   └── NotificationService.cs  # In-app + FCM push notifications
│   ├── Constants/
│   │   └── PlatformConstants.cs    # System fee percentage (5%)
│   ├── Migrations/                 # EF Core migrations
│   └── wwwroot/uploads/            # Uploaded files (venue images, avatars, proofs)
└── CLAUDE.md
```

---

## Commands
```bash
dotnet restore          # Restore packages
dotnet build            # Build
dotnet run -- --seed    # Seed database (drops + recreates + inserts data)
dotnet run              # Run server (port 8000)
dotnet ef migrations add <Name>  # Create new migration
```

API docs: `http://localhost:8000/openapi/v1.json`

---

## Auth flow
- Login → `POST /api/v1/auth/login` → returns `accessToken` in body + `refresh_token` in httpOnly cookie
- Access token: JWT (HS256), 15min expiry, sent as `Authorization: Bearer <token>`
- Refresh token: JWT, 7 days expiry, httpOnly cookie
- `POST /api/v1/auth/refresh` → reads cookie → returns new access token
- `POST /api/v1/auth/logout` → clears cookie
- Three roles: `super_admin`, `venue_owner`, `player`
- Rate limiting on auth endpoints

---

## Dev credentials
| Role | Email | Password |
|------|-------|----------|
| super_admin | amermohammed500@gmail.com | M7md.272 |
| venue_owner | khalid@venues.jo | M7md.272 |
| player | (register via app) | (user sets) |

---

## API response envelope
All endpoints return `ApiResponse<T>`:
```json
{
  "success": true,
  "data": {},
  "message": "string",
  "pagination": { "page": 1, "limit": 20, "total": 100 }
}
```
`pagination` is omitted when null (via `JsonIgnore(WhenWritingNull)`).

---

## Key endpoints
```
Auth
  POST   /api/v1/auth/login
  POST   /api/v1/auth/register
  POST   /api/v1/auth/refresh
  POST   /api/v1/auth/logout

Venues
  GET    /api/v1/venues?page=&limit=&search=&sport=&status=&owner_id=
  POST   /api/v1/venues
  GET    /api/v1/venues/{id}
  PATCH  /api/v1/venues/{id}
  DELETE /api/v1/venues/{id}
  GET    /api/v1/venues/{id}/stats
  GET    /api/v1/venues/public                    # No auth — active venues for map/list
  GET    /api/v1/venues/public/{id}               # No auth — single venue detail
  GET    /api/v1/venues/{id}/available-slots?date= # Available time slots

Users
  GET    /api/v1/users?page=&limit=&role=&search=  # Admin only
  POST   /api/v1/users                              # Admin only — create user
  PATCH  /api/v1/users/{id}/status                  # Admin only
  PATCH  /api/v1/users/{id}/role                    # Admin only
  PATCH  /api/v1/users/{id}/avatar                  # Admin only
  GET    /api/v1/users/me                            # Own profile
  PATCH  /api/v1/users/me                            # Update own profile
  PATCH  /api/v1/users/me/password                   # Change password

Bookings
  GET    /api/v1/bookings?page=&limit=&status=&venue_id=&from=&to=&owner_id=
  POST   /api/v1/bookings                           # Create booking
  GET    /api/v1/bookings/my                         # Player's own bookings
  GET    /api/v1/bookings/{id}                       # Single booking detail
  PATCH  /api/v1/bookings/{id}/confirm               # Owner confirms
  PATCH  /api/v1/bookings/{id}/cancel                # Cancel booking
  PATCH  /api/v1/bookings/{id}/complete              # Mark completed
  PATCH  /api/v1/bookings/{id}/no-show               # Mark no-show
  PATCH  /api/v1/bookings/{id}/upload-proof          # Upload Cliq payment proof
  PATCH  /api/v1/bookings/{id}/review-proof          # Owner approve/reject proof
  POST   /api/v1/bookings/recurring                  # Create recurring series
  PATCH  /api/v1/bookings/recurring/{groupId}/cancel # Cancel recurring series

Payments (admin only)
  GET    /api/v1/payments?page=&limit=&status=

Favorites
  GET    /api/v1/favorites                           # List user's favorites
  POST   /api/v1/favorites/{venueId}                 # Add to favorites
  DELETE /api/v1/favorites/{venueId}                  # Remove from favorites
  GET    /api/v1/favorites/check?venueIds=id1,id2    # Batch check

Notifications
  GET    /api/v1/notifications                       # User's notifications
  GET    /api/v1/notifications/unread-count
  PATCH  /api/v1/notifications/{id}/read
  POST   /api/v1/notifications/read-all
  POST   /api/v1/notifications/device-token          # Register FCM token
  DELETE /api/v1/notifications/device-token           # Deactivate FCM token
  GET    /api/v1/notifications/users                 # Admin — users with FCM info
  POST   /api/v1/notifications/send                  # Admin — send to users
  GET    /api/v1/notifications/templates             # Admin — template CRUD
  POST   /api/v1/notifications/templates
  PATCH  /api/v1/notifications/templates/{id}
  DELETE /api/v1/notifications/templates/{id}

Uploads
  POST   /api/v1/uploads                             # Multipart file upload

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
- `id`, `name`, `email` (unique), `phone`, `password_hash`, `role`, `status`, `avatar`, `permissions` (JSON), `created_at`

### Venue
- `id`, `name`, `owner_id` (FK), `sports` (JSON), `city`, `address`, `price_per_hour`, `status`, `description`, `images` (JSON), `latitude`, `longitude`, `cliq_alias`, `operating_hours` (JSON), `min_booking_duration`, `max_booking_duration`, `deposit_percentage`, `created_at`

### Booking
- `id`, `venue_id` (FK), `player_id` (FK), `sport`, `date`, `start_time`, `duration`, `amount`, `total_amount`, `deposit_amount`, `deposit_paid`, `amount_paid`, `system_fee_percentage`, `system_fee`, `owner_amount`, `payment_method`, `status`, `notes`, `payment_proof`, `payment_proof_status`, `payment_proof_note`, `recurring_group_id` (FK nullable), `created_at`

### Payment
- `id`, `booking_id` (FK), `player_id` (FK), `amount`, `method`, `status`, `date`

### RecurringBookingGroup
- `id`, `player_id` (FK), `venue_id` (FK), `sport`, `day_of_week`, `start_time`, `duration`, `recurrence_type`, `start_date`, `end_date`, `status`

### Notification
- `id`, `user_id` (FK), `title`, `body`, `type`, `reference_id`, `is_read`, `image`, `created_at`

### NotificationTemplate
- `id`, `name`, `title`, `body`, `type`, `created_at`

### DeviceToken
- `id`, `user_id` (FK), `token`, `platform`, `is_active`, `created_at`

### Favorite
- `id`, `user_id` (FK), `venue_id` (FK), `created_at`
- Unique index on (user_id, venue_id)

### LoyaltyProgram
- `id`, `venue_id` (FK unique), `bookings_required`, `reward_type`, `reward_value`, `is_active`, `created_at`

### LoyaltyPoints
- `id`, `user_id` (FK), `venue_id` (FK), `points`, `total_redeemed`

---

## Revenue split
- Platform fee: 5% (`PlatformConstants.SystemFeePercentage`)
- Owner receives 95% of each booking
- Fields: `system_fee`, `owner_amount` on Booking
- Admin sees full breakdown; owner sees only their cut

## Payment system
- **Stripe**: card payment via PaymentIntent (deposit amount)
- **CliQ**: player transfers to venue's CliQ alias, uploads proof screenshot, owner reviews (approve/reject)
- Default deposit: 20% of total (configurable per venue via `deposit_percentage`)

---

## Owner scoping
When `venue_owner` is logged in, the backend enforces `owner_id = current_user.id` on:
- `GET /venues` — only their venues
- `GET /bookings` — only bookings for their venues
- `GET /reports/summary` — only their stats

---

## Code conventions
- All controllers return `ApiResponse<T>` envelope
- Response fields: **camelCase** via `[JsonPropertyName]`
- Database columns: **snake_case** via `[Column]`
- `sports` and `images` on Venue stored as JSON strings with `[NotMapped]` List<string> accessors
- JWT claims: `sub` (user_id), `role`, `type` (access/refresh) — use `ClaimTypes.*` in controllers
- `[Authorize]` on all controllers except AuthController
- All `.Include()` queries use `.AsSplitQuery()` to avoid MySQL sort buffer overflow
- Count queries separated from data queries
- ILogger for all warning/error logging (no Console.WriteLine)
- Auto-migration on startup via `db.Database.MigrateAsync()`

---

## MySQL notes
- Requires MySQL 8.0+ (JSON column support)
- Sort buffer: `SET GLOBAL sort_buffer_size = 4194304;` if needed
- `.AsSplitQuery()` pattern prevents most sort buffer issues

---

## Seed data
- 15 users (DiceBear avatars)
- 8 venues (Jordan cities, real GPS coordinates, picsum images)
- 25 bookings (various statuses)
- 20 payments (various methods)

Run: `dotnet run -- --seed` (applies migrations + drops/recreates data)

---

## Connecting to clients
```bash
# Terminal 1: Backend
cd yalla-nhjez-api/SportsVenueApi
dotnet run                    # http://localhost:8000

# Terminal 2: Dashboard
cd yalla-nhjez-dashboard
npm run dev                   # http://localhost:5173

# Terminal 3: Flutter app
cd yalla-nhjez-app
flutter run
```
