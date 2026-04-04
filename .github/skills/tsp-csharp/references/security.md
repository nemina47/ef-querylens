# Security

## Purpose

Rules for authentication, authorization, input validation, secrets management, CORS, rate limiting, middleware ordering, and secure defaults in ASP.NET Core applications.

## Default Guidance

### Input Validation

- Validate **all** external input at system boundaries (API endpoints, form submissions, file uploads, query parameters)
- Use allowlists over denylists — define what IS valid, not what isn't
- Validate type, length, range, and format
- Use data annotations + `FluentValidation` for complex rules; `ValidateDataAnnotations()` + `ValidateOnStart()` for Options
- Never trust client-side validation alone — always validate server-side
- Dedicated request model for every endpoint — never bind directly to entity classes
- Request model validation must correlate with entity constraints (e.g., `[StringLength(100)]` on DTO matches `MaxLength(100)` on entity)
- DTO-to-entity mapping must be deliberate — map only essential fields; never blindly `AutoMap` all properties
- Fields that accept HTML must be explicitly marked and sanitized with `Ganss.Xss.HtmlSanitizer` — allow presentation tags only
- Use explicit parameter binding: `[FromQuery]`, `[FromBody]`, `[FromRoute]` — no ambiguous model binding

### Options Validation (Fail Fast)

- Always use `ValidateOnStart()` to detect invalid configuration at startup, not at first use

```csharp
public class DatabaseOptions
{
    public const string Section = "Database";

    [Required]
    public required string ConnectionString { get; init; }

    [Range(1, 100)]
    public int MaxPoolSize { get; init; } = 10;
}

builder.Services.AddOptions<DatabaseOptions>()
    .BindConfiguration(DatabaseOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();  // CRITICAL: fail at startup
```

- Use `IValidateOptions<T>` for cross-property or environment-specific validation

### Injection Prevention

- **SQL Injection**: always use parameterized queries or ORM — never string concatenation. Use `FromSqlInterpolated`, EF LINQ, Dapper parameters
- **XSS**: Razor auto-encodes by default; use `HtmlEncoder` for manual encoding. Avoid `innerHTML` / `dangerouslySetInnerHTML`
- **Command Injection**: never pass user input directly to shell commands or `Process.Start`
- **Path Traversal**: validate file paths against a known base directory; reject `..` segments

### Authentication & Authorization

- Use established libraries (ASP.NET Identity, OAuth/OIDC providers) — never roll your own auth
- Use policy-based authorization over role checks: `RequireAuthorization("PolicyName")`
- Validate JWT tokens server-side (signature, issuer, audience, expiration)
- Log authentication failures for monitoring

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://your-idp.com";
        options.Audience = "your-api";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5)
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", p => p.RequireRole("Admin"))
    .AddPolicy("CanReadOrders", p => p.RequireClaim("scope", "orders:read"));
```

### Data Ownership & Tenancy

- Every data-access endpoint must verify the requesting user owns or has access to the resource
- Fetch-by-ID must filter by tenant/owner — never rely solely on knowing the ID
- For multi-tenant systems: enforce tenant filter via EF global query filter or middleware — don't rely on individual queries
- Log ownership verification failures for security monitoring

```csharp
// Bad: anyone who knows the ID can access any order
var order = await context.Orders.FindAsync(orderId);

// Good: filter by authenticated user's ownership
var order = await context.Orders
    .Where(o => o.Id == orderId && o.TenantId == currentUser.TenantId)
    .FirstOrDefaultAsync(cancellationToken);
```

### CORS

- Configure restrictively — only allow known origins
- Use named policies and apply per-endpoint or globally
- Never use `AllowAnyOrigin()` in production

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy => policy
        .WithOrigins("https://app.example.com")
        .WithMethods("GET", "POST", "PUT", "DELETE")
        .WithHeaders("Content-Type", "Authorization")
        .AllowCredentials());
});
```

### Rate Limiting

- Use built-in rate limiting middleware (`AddRateLimiter`)
- Apply per-endpoint or per-group
- Return `ProblemDetails` on rejection (429)

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("api", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 100;
        opt.QueueLimit = 10;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
```

### Secrets Management

- **Never** store secrets in source code, committed config files, or environment variable defaults in code
- Use: Azure Key Vault > environment variables (set at deployment) > .NET User Secrets for local dev
- Use `IConfiguration` binding — never hardcode connection strings
- Rotate secrets on a schedule and on suspected compromise

### Data Protection

- Use `IDataProtectionProvider` for encrypting sensitive data at rest
- Enforce HTTPS for all data in transit
- Hash passwords with `bcrypt`, `scrypt`, or `Argon2` — never MD5/SHA
- Mask or redact sensitive data in logs (PII, tokens, passwords)

### Security Headers

- Set: `Content-Security-Policy`, `X-Content-Type-Options`, `X-Frame-Options`, `Strict-Transport-Security`
- Disable detailed error messages in production — use `ProblemDetails` for generic error responses

### Dependency Security

- Keep dependencies updated — review Dependabot / security advisories
- Pin major versions; use lockfiles (`packages.lock.json`)
- **Before adding any new package**, evaluate:
  - GitHub stars and community adoption
  - Release frequency — is maintenance active?
  - Open issues trend — are bugs being addressed?
  - Is there a built-in .NET alternative (`System.*`, `Microsoft.*`)?
  - Are there alternatives from larger, more established maintainers?
- Prefer standard library over third-party when functionality is comparable
- New library additions must be discussed and approved before merging

## Avoid

| Anti-Pattern | Fix |
| --- | --- |
| Missing `ValidateOnStart()` on Options | Always add — fail at startup, not first use |
| `FromSqlRaw` with string concatenation | `FromSqlInterpolated` |
| Rolling your own auth | ASP.NET Identity or established OIDC provider |
| `AllowAnyOrigin()` in production | Named CORS policy with specific origins |
| Secrets in `appsettings.json` committed to git | Key Vault, env vars, or User Secrets |
| `[Authorize(Roles = "Admin")]` | Policy-based: `RequireAuthorization("AdminOnly")` |
| Detailed error messages in production | `ProblemDetails` with generic titles |
| Logging tokens, passwords, PII | Mask/redact before logging |
| No rate limiting on public endpoints | `AddRateLimiter` with appropriate policy |
| No data ownership check on endpoint | Filter by tenant/owner — never rely solely on knowing the ID |
| Binding directly to entity models | Dedicated request/response DTOs per endpoint |
| Blind `AutoMap` of all properties | Map only essential fields deliberately |
| Missing `[FromQuery]`/`[FromBody]` decorators | Explicit parameter binding on every endpoint parameter |
| Adding packages without vetting | Check: stars, release cadence, open issues, built-in alternative |
| Accepting HTML without sanitization | Use `Ganss.Xss.HtmlSanitizer` — presentation tags only |

## Review Checklist

- [ ] All Options classes use `ValidateDataAnnotations()` + `ValidateOnStart()`
- [ ] All external input validated server-side at system boundaries
- [ ] Authentication and authorization configured — every endpoint has explicit auth policy
- [ ] CORS configured restrictively (no `AllowAnyOrigin` in production)
- [ ] Rate limiting applied to public-facing endpoints
- [ ] Middleware ordered correctly (see SKILL.md ordering)
- [ ] No secrets in source code or committed config
- [ ] SQL queries use parameterized methods only
- [ ] Security headers set for production
- [ ] Sensitive data masked in log output
- [ ] Data ownership verified — endpoints filter by tenant/owner, not just ID
- [ ] Dedicated request/response models per endpoint (not entity models)
- [ ] Request model validation correlates with entity constraints (e.g., `MaxLength` matches)
- [ ] HTML-accepting fields sanitized with `Ganss.Xss.HtmlSanitizer`
- [ ] Parameter binding explicit (`[FromQuery]`, `[FromBody]`, `[FromRoute]`)
- [ ] New packages vetted: stars, release frequency, open issues, built-in alternatives

## Related Files

- [Async](./async.md) — timeout patterns with linked CancellationTokenSource
- [EF Core](./ef-core.md) — parameterized queries, FromSqlInterpolated
- [Performance](./perf.md) — HTTP client resilience, streaming

## Source Anchors

- [ASP.NET Core security overview](https://learn.microsoft.com/en-us/aspnet/core/security/)
- [Authentication in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/)
- [Rate limiting middleware](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit)
- [Options pattern validation](https://learn.microsoft.com/en-us/dotnet/core/extensions/options#options-validation)
- [CORS in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/cors)
