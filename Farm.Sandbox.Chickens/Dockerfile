FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl git tini tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && install --directory --owner=1654 --group=1654 \
        /workspace/repository \
        /workspace/artifacts \
        /workspace/state \
        /workspace/cache \
        /workspace/worktrees \
        /workspace/agent-overrides \
    && git config --system --add safe.directory '*'

# Trust the corporate TLS-inspecting proxy CA (Cisco Umbrella) so outbound HTTPS calls
# from within the sandbox (e.g. Copilot CLI model resolution) don't fail with UnknownIssuer.
COPY deployment/cisco-umbrella-root-ca.crt /usr/local/share/ca-certificates/cisco-umbrella-root-ca.crt
RUN update-ca-certificates

# GitHub CLI (gh), installed from the official apt repo so `gh auth login` device-flow
# can populate /workspace/cache/home and be picked up by Copilot SDK's UseLoggedInUser fallback.
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

# Vendored GitHub.Copilot.SDK native runtime (built via /p:CopilotRuntimeIdentifier=linux-x64)
# lands at /app/runtimes/linux-x64/native/copilot. It is an SDK host, not a CLI: no
# --help/auth/login subcommands, only --server/--headless/--port/--host/--stdio.
COPY --chown=1654:1654 artifacts/farm-sandbox-chickens/linux-x64/ ./

USER 1654:1654

ENTRYPOINT ["dotnet", "Farm.Sandbox.Chickens.dll"]