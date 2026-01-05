using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseInMemoryDatabase("UserDatabase")
    .EnableSensitiveDataLogging(false)
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IUserValidator, UserValidator>();
builder.Services.AddMemoryCache(options =>
{
    options.CompactionPercentage = 0.25;
    options.ExpirationScanFrequency = TimeSpan.FromMinutes(5);
});

// Configurar JWT Authentication
var jwtKey = "your-super-secret-key-change-this-in-production-at-least-32-characters-long";
var jwtIssuer = "UserManagementAPI";
var jwtAudience = "UserManagementAPIUsers";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                System.Diagnostics.Debug.WriteLine($"[JWT] Autenticación fallida: {context.Exception.Message}");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsJsonAsync(new
                {
                    error = "Token inválido o expirado",
                    statusCode = StatusCodes.Status401Unauthorized,
                    timestamp = DateTime.UtcNow
                });
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsJsonAsync(new
                {
                    error = "Token requerido o no autorizado",
                    statusCode = StatusCodes.Status401Unauthorized,
                    timestamp = DateTime.UtcNow
                });
            }
        };
    });

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Agregar response compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

var app = builder.Build();

app.UseResponseCompression();

// Middleware de logging de auditoría
app.Use(async (context, next) =>
{
    var request = context.Request;
    var timestamp = DateTime.UtcNow;
    
    // Capturar la respuesta
    var originalBodyStream = context.Response.Body;
    using (var responseBody = new MemoryStream())
    {
        context.Response.Body = responseBody;
        
        await next();
        
        var response = context.Response;
        
        // Registrar información de solicitud y respuesta
        var log = new
        {
            Timestamp = DateTime.UtcNow,
            Method = request.Method,
            Path = request.Path.Value,
            StatusCode = response.StatusCode,
            Duration = (DateTime.UtcNow - timestamp).TotalMilliseconds
        };
        
        System.Diagnostics.Debug.WriteLine($"[HTTP] {System.Text.Json.JsonSerializer.Serialize(log)}");
        
        await responseBody.CopyToAsync(originalBodyStream);
    }
    
    context.Response.Body = originalBodyStream;
});

// Middleware global de manejo de excepciones
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var statusCode = StatusCodes.Status500InternalServerError;
        var errorMessage = "Internal server error.";
        object? errorDetails = null;

        if (exception != null)
        {
            switch (exception)
            {
                case ArgumentNullException argNullEx:
                    statusCode = StatusCodes.Status400BadRequest;
                    errorMessage = "Invalid request parameters.";
                    errorDetails = new { detail = argNullEx.ParamName };
                    break;

                case InvalidOperationException invalidOpEx:
                    statusCode = StatusCodes.Status400BadRequest;
                    errorMessage = invalidOpEx.Message ?? "Invalid operation.";
                    break;

                case DbUpdateException dbEx:
                    statusCode = StatusCodes.Status500InternalServerError;
                    errorMessage = "Database operation failed.";
                    break;

                case TimeoutException:
                    statusCode = StatusCodes.Status408RequestTimeout;
                    errorMessage = "Request timeout.";
                    break;

                default:
                    statusCode = StatusCodes.Status500InternalServerError;
                    errorMessage = "Internal server error.";
                    break;
            }

            System.Diagnostics.Debug.WriteLine($"[EXCEPTION] {exception.GetType().Name}: {exception.Message}");
        }

        var errorResponse = new
        {
            error = errorMessage,
            statusCode = statusCode,
            timestamp = DateTime.UtcNow
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(errorResponse);
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Minimal API endpoints para gestión de usuarios
var userGroup = app.MapGroup("/api/users")
    .WithName("Users")
    .WithOpenApi();

// GET: Recuperar todos los usuarios con paginación
userGroup.MapGet("/", GetAllUsers)
    .WithName("GetAllUsers")
    .WithOpenApi()
    .CacheOutput(p => p.Expire(TimeSpan.FromMinutes(1)))
    .Produces<PaginatedResponse<UserDto>>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status500InternalServerError);

// GET: Recuperar usuario por ID
userGroup.MapGet("/{id}", GetUserById)
    .WithName("GetUserById")
    .WithOpenApi()
    .Produces(StatusCodes.Status500InternalServerError);

// POST: Crear nuevo usuario
userGroup.MapPost("/", CreateUser)
    .WithName("CreateUser")
    .WithOpenApi()
    .Produces(StatusCodes.Status500InternalServerError);

// PUT: Actualizar usuario existente
userGroup.MapPut("/{id}", UpdateUser)
    .WithName("UpdateUser")
    .WithOpenApi()
    .Produces(StatusCodes.Status500InternalServerError);

// DELETE: Eliminar usuario
userGroup.MapDelete("/{id}", DeleteUser)
    .WithName("DeleteUser")
    .WithOpenApi()
    .Produces(StatusCodes.Status500InternalServerError);

// GET: Buscar usuarios por criterios
userGroup.MapGet("/search/{query}", SearchUsers)
    .WithName("SearchUsers")
    .WithOpenApi()
    .Produces<List<UserDto>>(StatusCodes.Status200OK);

app.Run();

// Handlers
async Task<IResult> GetAllUsers(IUserService userService, int page = 1, int pageSize = 10)
{
    try
    {
        if (page < 1 || pageSize < 1)
            return Results.BadRequest(new { message = "page y pageSize deben ser mayores a 0" });
        
        if (pageSize > 100)
            return Results.BadRequest(new { message = "pageSize máximo es 100" });
        
        var result = await userService.GetAllUsersAsync(page, pageSize);
        return Results.Ok(result);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
}

async Task<IResult> GetUserById(int id, IUserService userService)
{
    try
    {
        if (id <= 0)
            return Results.BadRequest(new { message = "El ID debe ser mayor a 0" });
        
        var user = await userService.GetUserByIdAsync(id);
        if (user == null)
            return Results.NotFound(new { message = "Usuario no encontrado" });
        
        return Results.Ok(user);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
}

async Task<IResult> CreateUser(CreateUserDto dto, IUserService userService, IUserValidator validator)
{
    try
    {
        if (dto == null)
            return Results.BadRequest(new { message = "Los datos del usuario son requeridos" });

        var validationResult = validator.ValidateCreateUser(dto);
        if (!validationResult.IsValid)
            return Results.BadRequest(new { message = validationResult.Message, errors = validationResult.Errors });
        
        var user = await userService.CreateUserAsync(dto);
        return Results.Created($"/api/users/{user.Id}", user);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = "Email ya registrado" });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
}

async Task<IResult> UpdateUser(int id, UpdateUserDto dto, IUserService userService, IUserValidator validator)
{
    try
    {
        if (id <= 0)
            return Results.BadRequest(new { message = "El ID debe ser mayor a 0" });
        
        if (dto == null)
            return Results.BadRequest(new { message = "Los datos de actualización son requeridos" });

        var validationResult = validator.ValidateUpdateUser(dto);
        if (!validationResult.IsValid)
            return Results.BadRequest(new { message = validationResult.Message, errors = validationResult.Errors });
        
        var user = await userService.UpdateUserAsync(id, dto);
        if (user == null)
            return Results.NotFound(new { message = "Usuario no encontrado" });
        
        return Results.Ok(user);
    }
    catch (InvalidOperationException)
    {
        return Results.BadRequest(new { message = "Email ya registrado" });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
}

async Task<IResult> DeleteUser(int id, IUserService userService)
{
    try
    {
        if (id <= 0)
            return Results.BadRequest(new { message = "El ID debe ser mayor a 0" });
        
        var success = await userService.DeleteUserAsync(id);
        if (!success)
            return Results.NotFound(new { message = "Usuario no encontrado" });
        
        return Results.NoContent();
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
}

async Task<IResult> SearchUsers(string query, IUserService userService)
{
    try
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Results.BadRequest(new { message = "La búsqueda debe tener al menos 2 caracteres" });
        
        var results = await userService.SearchUsersAsync(query);
        return Results.Ok(results);
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
}

// Models
public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public static UserDto FromUser(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt
        };
    }
}

public class CreateUserDto
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class UpdateUserDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

public class PaginatedResponse<T>
{
    public List<T> Data { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => (TotalCount + PageSize - 1) / PageSize;
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

public class ValidationResult
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}

// Database Context
public class UserDbContext : DbContext
{
    public UserDbContext(DbContextOptions<UserDbContext> options) : base(options) { }
    
    public DbSet<User> Users { get; set; } = null!;
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        try
        {
            base.OnModelCreating(modelBuilder);
            
            // Índice en Email (único)
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique();
            
            // Índice en CreatedAt para ordenamiento
            modelBuilder.Entity<User>()
                .HasIndex(u => u.CreatedAt);
            
            // Índice composite para búsqueda
            modelBuilder.Entity<User>()
                .HasIndex(u => new { u.Name, u.Email });
            
            modelBuilder.Entity<User>()
                .Property(u => u.Name)
                .IsRequired()
                .HasMaxLength(100);
            
            modelBuilder.Entity<User>()
                .Property(u => u.Email)
                .IsRequired()
                .HasMaxLength(255);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error configurando modelo", ex);
        }
    }
}

// Validator Interface
public interface IUserValidator
{
    ValidationResult ValidateCreateUser(CreateUserDto dto);
    ValidationResult ValidateUpdateUser(UpdateUserDto dto);
    ValidationResult ValidateEmail(string email);
    ValidationResult ValidateName(string name, bool allowEmpty = false);
}

// Validator Implementation
public class UserValidator : IUserValidator
{
    private const int MIN_NAME_LENGTH = 2;
    private const int MAX_NAME_LENGTH = 100;
    private const int MIN_EMAIL_LENGTH = 5;
    private const int MAX_EMAIL_LENGTH = 255;
    private static readonly Regex EmailRegex = new(
        @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex NameRegex = new(
        @"^[a-zA-Z0-9\s\-'áéíóúñ]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    public ValidationResult ValidateCreateUser(CreateUserDto dto)
    {
        var errors = new List<string>();

        var nameResult = ValidateName(dto.Name, allowEmpty: false);
        if (!nameResult.IsValid)
            errors.AddRange(nameResult.Errors);

        var emailResult = ValidateEmail(dto.Email);
        if (!emailResult.IsValid)
            errors.AddRange(emailResult.Errors);

        return errors.Count == 0
            ? new ValidationResult { IsValid = true }
            : new ValidationResult 
            { 
                IsValid = false, 
                Message = "Errores en validación",
                Errors = errors 
            };
    }

    public ValidationResult ValidateUpdateUser(UpdateUserDto dto)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(dto.Name) && string.IsNullOrWhiteSpace(dto.Email))
        {
            return new ValidationResult 
            { 
                IsValid = false, 
                Message = "Debe proporcionar al menos un campo",
                Errors = new List<string> { "Nombre o email requerido" }
            };
        }

        if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            var nameResult = ValidateName(dto.Name, allowEmpty: false);
            if (!nameResult.IsValid)
                errors.AddRange(nameResult.Errors);
        }

        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            var emailResult = ValidateEmail(dto.Email);
            if (!emailResult.IsValid)
                errors.AddRange(emailResult.Errors);
        }

        return errors.Count == 0
            ? new ValidationResult { IsValid = true }
            : new ValidationResult 
            { 
                IsValid = false, 
                Message = "Errores en validación",
                Errors = errors 
            };
    }

    public ValidationResult ValidateEmail(string email)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(email))
        {
            errors.Add("El email no puede estar vacío");
            return new ValidationResult { IsValid = false, Message = "Email inválido", Errors = errors };
        }

        email = email.Trim();

        if (email.Length < MIN_EMAIL_LENGTH || email.Length > MAX_EMAIL_LENGTH)
            errors.Add($"Email debe tener entre {MIN_EMAIL_LENGTH} y {MAX_EMAIL_LENGTH} caracteres");

        if (!EmailRegex.IsMatch(email))
            errors.Add("Formato de email inválido");

        return errors.Count == 0
            ? new ValidationResult { IsValid = true }
            : new ValidationResult { IsValid = false, Message = "Email inválido", Errors = errors };
    }

    public ValidationResult ValidateName(string name, bool allowEmpty = false)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            if (!allowEmpty)
                errors.Add("El nombre no puede estar vacío");
            
            return errors.Count == 0
                ? new ValidationResult { IsValid = true }
                : new ValidationResult { IsValid = false, Message = "Nombre inválido", Errors = errors };
        }

        name = name.Trim();

        if (name.Length < MIN_NAME_LENGTH || name.Length > MAX_NAME_LENGTH)
            errors.Add($"Nombre debe tener entre {MIN_NAME_LENGTH} y {MAX_NAME_LENGTH} caracteres");

        if (!NameRegex.IsMatch(name))
            errors.Add("Nombre contiene caracteres no permitidos");

        if (Regex.IsMatch(name, @"\s{2,}"))
            errors.Add("Nombre no puede tener espacios múltiples");

        if (Regex.IsMatch(name, @"^\d+$"))
            errors.Add("Nombre no puede ser solo números");

        return errors.Count == 0
            ? new ValidationResult { IsValid = true }
            : new ValidationResult { IsValid = false, Message = "Nombre inválido", Errors = errors };
    }
}

// Service Interface
public interface IUserService
{
    Task<PaginatedResponse<UserDto>> GetAllUsersAsync(int page = 1, int pageSize = 10);
    Task<UserDto?> GetUserByIdAsync(int id);
    Task<UserDto> CreateUserAsync(CreateUserDto dto);
    Task<UserDto?> UpdateUserAsync(int id, UpdateUserDto dto);
    Task<bool> DeleteUserAsync(int id);
    Task<List<UserDto>> SearchUsersAsync(string query);
}

// Service Implementation
public class UserService : IUserService
{
    private readonly UserDbContext _context;
    private readonly IMemoryCache _cache;
    private const string CACHE_KEY_USERS = "users_page";
    private const string CACHE_KEY_USER = "user";
    private const string CACHE_KEY_SEARCH = "search";
    private const int CACHE_DURATION_MINUTES = 5;

    public UserService(UserDbContext context, IMemoryCache cache)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<PaginatedResponse<UserDto>> GetAllUsersAsync(int page = 1, int pageSize = 10)
    {
        try
        {
            string cacheKey = $"{CACHE_KEY_USERS}_{page}_{pageSize}";
            if (_cache.TryGetValue(cacheKey, out PaginatedResponse<UserDto>? cachedResult))
            {
                return cachedResult!;
            }

            // Usar LINQ para contar sin cargar datos
            var totalCount = await _context.Users.CountAsync();
            
            // Proyección directa a DTO para reducir datos transferidos
            var users = await _context.Users
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new UserDto
                {
                    Id = u.Id,
                    Name = u.Name,
                    Email = u.Email,
                    CreatedAt = u.CreatedAt,
                    UpdatedAt = u.UpdatedAt
                })
                .ToListAsync();

            var result = new PaginatedResponse<UserDto>
            {
                Data = users,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            };

            _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));

            return result;
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("Error BD", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error obteniendo usuarios", ex);
        }
    }

    public async Task<UserDto?> GetUserByIdAsync(int id)
    {
        try
        {
            string cacheKey = $"{CACHE_KEY_USER}_{id}";
            if (_cache.TryGetValue(cacheKey, out UserDto? cachedUser))
            {
                return cachedUser;
            }

            var user = await _context.Users
                .Where(u => u.Id == id)
                .Select(u => new UserDto
                {
                    Id = u.Id,
                    Name = u.Name,
                    Email = u.Email,
                    CreatedAt = u.CreatedAt,
                    UpdatedAt = u.UpdatedAt
                })
                .FirstOrDefaultAsync();

            if (user != null)
            {
                _cache.Set(cacheKey, user, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
            }

            return user;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error obteniendo usuario", ex);
        }
    }

    public async Task<UserDto> CreateUserAsync(CreateUserDto dto)
    {
        try
        {
            var existingUser = await _context.Users
                .AnyAsync(u => u.Email == dto.Email);
            
            if (existingUser)
                throw new InvalidOperationException("Email registrado");

            var user = new User
            {
                Name = dto.Name.Trim(),
                Email = dto.Email.Trim().ToLowerInvariant(),
                CreatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            
            InvalidateCache();
            
            return UserDto.FromUser(user);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("Error BD", ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error creando usuario", ex);
        }
    }

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserDto dto)
    {
        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null)
                return null;

            if (!string.IsNullOrWhiteSpace(dto.Email) && dto.Email != user.Email)
            {
                var existingUser = await _context.Users
                    .AnyAsync(u => u.Email == dto.Email.Trim().ToLowerInvariant() && u.Id != id);
                
                if (existingUser)
                    throw new InvalidOperationException("Email registrado");
            }

            if (!string.IsNullOrWhiteSpace(dto.Name))
                user.Name = dto.Name.Trim();
            
            if (!string.IsNullOrWhiteSpace(dto.Email))
                user.Email = dto.Email.Trim().ToLowerInvariant();
            
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            
            InvalidateCache();
            
            return UserDto.FromUser(user);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("Error BD", ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error actualizando", ex);
        }
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null)
                return false;

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            
            InvalidateCache();
            
            return true;
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("Error BD", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error eliminando", ex);
        }
    }

    public async Task<List<UserDto>> SearchUsersAsync(string query)
    {
        try
        {
            string cacheKey = $"{CACHE_KEY_SEARCH}_{query.ToLowerInvariant()}";
            if (_cache.TryGetValue(cacheKey, out List<UserDto>? cachedResults))
            {
                return cachedResults!;
            }

            var searchTerm = query.ToLowerInvariant().Trim();
            
            var results = await _context.Users
                .Where(u => u.Name.ToLower().Contains(searchTerm) || u.Email.ToLower().Contains(searchTerm))
                .OrderByDescending(u => u.CreatedAt)
                .Take(50)
                .Select(u => new UserDto
                {
                    Id = u.Id,
                    Name = u.Name,
                    Email = u.Email,
                    CreatedAt = u.CreatedAt,
                    UpdatedAt = u.UpdatedAt
                })
                .ToListAsync();

            _cache.Set(cacheKey, results, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));

            return results;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error buscando usuarios", ex);
        }
    }

    private void InvalidateCache()
    {
        try
        {
            _cache.Remove(CACHE_KEY_USERS);
        }
        catch (Exception) { }
    }
}