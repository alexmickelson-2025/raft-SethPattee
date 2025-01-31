FROM mcr.microsoft.com/dotnet/sdk:9.0
 
RUN apt-get update && \
    apt-get install -y curl && \
    rm -rf /var/lib/apt/lists/*
 
 
RUN groupadd -g 1000 developer && \
    useradd -r -u 1000 -g developer -m -s /usr/bin/bash developer && \
    mkdir -p /home/developer && \
    chown -R developer:developer /home/developer
 
USER developer:developer