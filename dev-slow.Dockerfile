FROM mcr.microsoft.com/dotnet/sdk:9.0
 
RUN apt-get update && \
    apt-get install -y curl && \
    rm -rf /var/lib/apt/lists/*
 
 
WORKDIR /app
 
COPY . /app
 
CMD dotnet run --project RaftWeb