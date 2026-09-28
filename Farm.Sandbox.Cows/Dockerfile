# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl git tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && install --directory --owner=1654 --group=1654 \
        /workspace/repository \
        /workspace/notes \
        /workspace/state \
        /workspace/cache \
    && git config --system --add safe.directory '*'

# Trust the corporate TLS-inspecting proxy CA (Cisco Umbrella) so outbound HTTPS calls
# from within the sandbox (e.g. Copilot model resolution, MCP servers) don't fail with UnknownIssuer.
# The CA is gitignored and optional (absent in CI); the bind mount keeps it out of image layers.
RUN --mount=type=bind,source=deployment,target=/tmp/deploy \
    if [ -f /tmp/deploy/cisco-umbrella-root-ca.crt ]; then \
        cp /tmp/deploy/cisco-umbrella-root-ca.crt /usr/local/share/ca-certificates/; \
    fi \
    && update-ca-certificates

# GitHub CLI (gh) so Copilot SDK's UseLoggedInUser can read the shared gh login
# (hosts.yml bind-mounted read-only at $HOME/.config/gh/hosts.yml by the compose file).
RUN curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg \
    && chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install --yes --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

ENV DOTNET_CLI_HOME=/workspace/cache/dotnet \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_NOLOGO=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    GIT_TERMINAL_PROMPT=0 \
    HOME=/workspace/cache/home \
    NUGET_PACKAGES=/workspace/cache/nuget

# Publish output includes resources/ (mcp.json + feature-investigation SKILL.md) and the vendored
# GitHub.Copilot.SDK native runtime at /app/runtimes/linux-x64/native/copilot.
COPY --chown=1654:1654 artifacts/farm-sandbox-cows/linux-x64/ ./

USER 1654:1654

ENTRYPOINT ["dotnet", "Farm.Sandbox.Cows.dll"]
