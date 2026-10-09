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

# Cài đặt thư viện fontconfig và fonts cơ bản để QuestPDF / SkiaSharp xuất PDF chuẩn trên Linux/Render
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Cấu hình cổng cho Render (hỗ trợ cả 8080 và 10000)
ENV ASPNETCORE_HTTP_PORTS=8080;10000
EXPOSE 8080
EXPOSE 10000

ENTRYPOINT ["dotnet", "InvoiceManager.dll"]
