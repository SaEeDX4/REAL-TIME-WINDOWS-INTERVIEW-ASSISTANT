# Build context: repository root.  docker build -f src/InterviewAssistant.Backend/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/InterviewAssistant.Contracts/ src/InterviewAssistant.Contracts/
COPY src/InterviewAssistant.Core/ src/InterviewAssistant.Core/
COPY src/InterviewAssistant.Backend/ src/InterviewAssistant.Backend/
RUN dotnet publish src/InterviewAssistant.Backend -c Release -o /out -p:TreatWarningsAsErrors=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
# Non-root user provided by the official image. No secrets are baked in: all configuration comes from environment variables
# (ConnectionStrings__Db, Auth__*, OpenAI__ApiKey, Paddle__*) injected by the hosting platform's secret store.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "InterviewAssistant.Backend.dll"]
