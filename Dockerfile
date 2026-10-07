# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["InvoiceManager/InvoiceManager.csproj", "InvoiceManager/"]
RUN dotnet restore "InvoiceManager/InvoiceManager.csproj"

COPY InvoiceManager/ InvoiceManager/
WORKDIR "/src/InvoiceManager"
RUN dotnet publish "InvoiceManager.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Cấu hình cổng cho Render (mặc định .NET 8 lắng nghe 8080)
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "InvoiceManager.dll"]
