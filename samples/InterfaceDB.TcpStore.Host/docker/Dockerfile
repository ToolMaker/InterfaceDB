FROM mcr.microsoft.com/dotnet/runtime:10.0

WORKDIR /app
RUN mkdir -p /var/lib/interfacedb && chown app:app /var/lib/interfacedb

# Build context contains only the prepared Linux publish output, never source or certificates.
COPY --chown=app:app . ./

USER app
EXPOSE 7443
ENTRYPOINT ["dotnet", "InterfaceDB.TcpStore.Host.dll", "/run/interfacedb/hostsettings.json"]
