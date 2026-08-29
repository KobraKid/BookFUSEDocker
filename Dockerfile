# Stage 1: Build BookFUSE application
FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:86fe223b90220ec8607652914b1d7dc56fc8ff422ca1240bb81e54c4b06509e6 AS bookfuse_build
WORKDIR /App
# Copy all project files
COPY BookFUSELinux/*.sln ./BookFUSE/
COPY BookFUSELinux/*.csproj ./BookFUSE/
COPY Tmds.Fuse/src/Tmds.Fuse/*.csproj ./Tmds.Fuse/src/Tmds.Fuse/
# Add Tmds.Fuse NuGet server
RUN dotnet nuget add source https://www.myget.org/F/tmds/api/v3/index.json
# Restore dependencies
RUN dotnet restore BookFUSE/BookFUSE.csproj

# Copy all source code
COPY BookFUSELinux/ ./BookFUSE/
COPY Tmds.Fuse/ ./Tmds.Fuse/

# Publish BookFUSE for release
RUN dotnet publish BookFUSE/BookFUSE.csproj -c Release -o out

# Stage 2: Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0@sha256:7ccab69cb986ab83c359552c86e9cef2b2238e7c4b75a75a7b60a3e26c1bc3cd AS final
WORKDIR /bookfuse

# Install necessary dependencies:
# fuse3 and libfuse3-dev for FUSE filesystem operations
# tini for proper signal handling as PID 1 in the container
RUN apt-get update && \
    apt-get install -y fuse3 libfuse3-dev tini && \
    echo "user_allow_other" >> /etc/fuse.conf && \
    rm -rf /var/lib/apt/lists/* ; # Clean up apt cache to reduce image size

# Create the mount point for BookFUSE
RUN mkdir -p /mnt/bookfuse

# Copy the published BookFUSE application into the final image
COPY --from=bookfuse_build /App/out ./

# Set Tini as the entrypoint to handle signals gracefully
ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["dotnet", "BookFUSE.dll"]