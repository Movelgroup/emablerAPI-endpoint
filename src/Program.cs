
using apiEndpointNameSpace.Services;
using apiEndpointNameSpace.Interfaces;
using NReco.Logging.File;
using Newtonsoft.Json.Linq;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.SecretManager.V1;


namespace apiEndpointNameSpace
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configure logging
            ConfigureLogging(builder);

            var firestoreDb = InitializeFirestoreDb(builder.Configuration);
            InitializeFirebaseAuth(builder.Configuration);

            // Add services to the container.
            ConfigureServices(builder.Services, firestoreDb, builder.Configuration);

            var app = builder.Build();

            app.Logger.LogInformation("Starting web application");

            // Configure the HTTP request pipeline.
            ConfigureApp(app);

            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            app.Urls.Add($"http://0.0.0.0:{port}");

            app.Run();

        }

        private static FirestoreDb InitializeFirestoreDb(IConfiguration configuration)
        {
            string projectId = configuration["GoogleCloudProjectId"]
                ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
                ?? throw new InvalidOperationException("GoogleCloudProjectId is not set");

            // In Cloud Run, we'll use the default service account
            if (Environment.GetEnvironmentVariable("K_SERVICE") != null)
            {
                return new FirestoreDbBuilder
                {
                    ProjectId = projectId,
                    // In Cloud Run, we don't need to specify credentials
                    // It will use the default service account
                }.Build();
            }

            // Local development with service account file
            string? credentialsJson = Environment.GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT");
            if (!string.IsNullOrEmpty(credentialsJson))
            {
                // Create a temporary file for the credentials
                var tempPath = Path.GetTempFileName();
                File.WriteAllText(tempPath, credentialsJson);

                return new FirestoreDbBuilder
                {
                    ProjectId = projectId,
                    CredentialsPath = tempPath
                }.Build();
            }

            // Fallback to local credentials file
            string credentialsPath = configuration["GoogleApplicationCredentials"]
                ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")
                ?? throw new InvalidOperationException("No credentials available");

            return new FirestoreDbBuilder
            {
                ProjectId = projectId,
                CredentialsPath = credentialsPath
            }.Build();
        }


        private static void ConfigureLogging(WebApplicationBuilder builder)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();
            builder.Logging.AddEventSourceLogger();
            builder.Logging.AddFile(builder.Configuration.GetSection("Logging"));
        }

        private static void InitializeFirebaseAuth(IConfiguration configuration)
        {
            if (FirebaseApp.DefaultInstance != null) return;

            // In Cloud Run
            if (Environment.GetEnvironmentVariable("K_SERVICE") != null)
            {
                FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.GetApplicationDefault(),
                    ProjectId = configuration["GoogleCloudProjectId"]
                });
                return;
            }

            // Local development with service account JSON
            string? credentialsJson = Environment.GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT");
            if (!string.IsNullOrEmpty(credentialsJson))
            {
                var credential = GoogleCredential.FromJson(credentialsJson);
                FirebaseApp.Create(new AppOptions
                {
                    Credential = credential,
                    ProjectId = configuration["GoogleCloudProjectId"]
                });
                return;
            }

            // Fallback to local credentials file
            string credentialsPath = configuration["GoogleApplicationCredentials"]
                ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")
                ?? throw new InvalidOperationException("No credentials available");

            FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromFile(credentialsPath),
                ProjectId = configuration["GoogleCloudProjectId"]
            });
        }




        public static void ConfigureServices(IServiceCollection services, FirestoreDb firestoreDb, IConfiguration configuration)
        {
            // Add this logging at the start to debug configuration
            var jwtKey = configuration["Jwt:Key"];
            var jwtIssuer = configuration["Jwt:Issuer"];
            var jwtAudience = configuration["Jwt:Audience"];
            
            if (string.IsNullOrEmpty(jwtKey) || string.IsNullOrEmpty(jwtIssuer) || string.IsNullOrEmpty(jwtAudience))
            {
                throw new InvalidOperationException($"JWT Configuration missing. Key: {jwtKey != null}, Issuer: {jwtIssuer != null}, Audience: {jwtAudience != null}");
            }

            services.AddSingleton(firestoreDb);
            services.AddControllers();
            services.AddEndpointsApiExplorer();
            services.AddCors(options =>
                {
                    options.AddPolicy("CorsPolicy", builder =>
                    {
                        builder
                            .WithOrigins(
                                "http://localhost:3000",
                                "https://movelsoftwaremanager.web.app",
                                "https://movelsoftwaremanager.firebaseapp.com")  // Firebase hosting alternate domain
                            .AllowAnyMethod()
                            .AllowAnyHeader()
                            .AllowCredentials()
                            .WithHeaders("Authorization", "Content-Type", "Accept", "Origin")
                            .WithMethods("GET", "POST", "OPTIONS"); // Add OPTIONS method explicitly
                            //.SetIsOriginAllowed(_ => true); // TODO: remove in production
                    });
                });

            // Configure JWT Authentication
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"],
                    ValidAudience = configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key not configured")))
                };

                // Configure JWT Bearer events for SignalR
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chargerhub"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });
            services.AddSwaggerGen();
            services.AddSingleton<IDataProcessor, DataProcessorService>();
            services.AddSingleton<IFirestoreService>(sp => 
            {
                var logger = sp.GetRequiredService<ILogger<FirestoreService>>();
                return new FirestoreService(firestoreDb, logger);
            });
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<IFirebaseAuthService, FirebaseAuthService>();
            services.AddSignalR(option =>
            {
                option.EnableDetailedErrors = true;
            });

            
        }


        public static void ConfigureApp(WebApplication app)
        {
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseCors("CorsPolicy");
            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();
            
            app.UseWebSockets();
            app.UseHttpsRedirection();

            app.MapControllers();
            app.MapHub<ChargerHub>("/chargerhub")
                .RequireCors("CorsPolicy");

            app.Logger.LogInformation("SignalR Hub mapped at: /chargerhub");
        }
    }
}