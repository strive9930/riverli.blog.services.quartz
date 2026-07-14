# =============================================================================
# Dockerfile — riverli-blog-quartz (定时任务调度服务)
# =============================================================================
# 构建命令（在解决方案根目录执行）：
#   docker build -t riverli-blog-quartz -f RiverLi.Blog.Services.Quartz.Api/Dockerfile .
# 说明：本服务为单项目解决方案，无内部类库依赖，Dockerfile 结构较简单
# =============================================================================

# ─── 阶段 1：构建环境（需完整 .NET SDK，包含编译工具链） ───
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# 提前复制 .csproj 文件以实现 Docker 层缓存优化
COPY ["RiverLi.Blog.Services.Quartz.Api/RiverLi.Blog.Services.Quartz.Api.csproj", "RiverLi.Blog.Services.Quartz.Api/"]

# 恢复 NuGet 依赖包（包括 Quartz、MySqlConnector、RiverLi.Blog.Infrastructure.Shared 等）
RUN dotnet restore "RiverLi.Blog.Services.Quartz.Api/RiverLi.Blog.Services.Quartz.Api.csproj"

# 复制整个解决方案的源代码
COPY . .

# 切换到 Api 项目目录并发布
WORKDIR "/src/RiverLi.Blog.Services.Quartz.Api"
RUN dotnet publish "RiverLi.Blog.Services.Quartz.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ─── 阶段 2：极简运行环境（仅包含 ASP.NET 运行时） ───
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RiverLi.Blog.Services.Quartz.Api.dll"]
