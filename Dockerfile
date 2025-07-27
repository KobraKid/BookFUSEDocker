# Stage 1: Build BookFUSE application
FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:86fe223b90220ec8607652914b1d7dc56fc8ff422ca1240bb81e54c4b06509e6 AS bookfuse_build
WORKDIR /App
# Copy all project files
COPY . ./
# Add Tmds.Fuse NuGet server
RUN dotnet nuget add source https://www.myget.org/F/tmds/api/v3/index.json
# Restore dependencies
RUN dotnet restore
# Publish BookFUSE for release
RUN dotnet publish -o out --no-restore

# Stage 2: Extract Kavita runtime files from its official image
FROM jvmilazz0/kavita:latest AS kavita_runtime
WORKDIR /

# Stage 3: Final combined runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0@sha256:7ccab69cb986ab83c359552c86e9cef2b2238e7c4b75a75a7b60a3e26c1bc3cd AS final
WORKDIR /App

# Install necessary dependencies:
# fuse3 and libfuse3-dev for FUSE filesystem operations
# supervisor for managing multiple processes (BookFUSE and Kavita)
# tini for proper signal handling as PID 1 in the container
RUN apt-get update && \
    apt-get install -y fuse3 libfuse3-dev supervisor tini && \
    rm -rf /var/lib/apt/lists/* ; # Clean up apt cache to reduce image size

# Create the mount point for BookFUSE
RUN mkdir /mnt/bookfuse

# Copy the published BookFUSE application into the final image
COPY --from=bookfuse_build /App/out /bookfuse

# Copy the Kavita runtime files into the final image
COPY --from=kavita_runtime /kavita /kavita
COPY --from=kavita_runtime entrypoint.sh /entrypoint.sh
COPY --from=kavita_runtime tmp/config/appsettings.json /tmp/config/appsettings.json

# Replicate Kavita's environment variables
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV TZ=UTC

# Create a directory for supervisord logs
RUN mkdir -p /var/log/supervisor

# Copy the supervisord configuration file into the container
COPY supervisord.conf /etc/supervisor/conf.d/supervisord.conf

# Expose Kavita's default port
EXPOSE 5000
HEALTHCHECK --interval=30s --timeout=15s --start-period=30s --retries=5 CMD "curl -fsS http://localhost:5000/api/health || exit 1"

# Set Tini as the entrypoint to handle signals gracefully
# Tini will then execute supervisord, which manages BookFUSE and Kavita
ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["/usr/bin/supervisord", "-c", "/etc/supervisor/conf.d/supervisord.conf"]