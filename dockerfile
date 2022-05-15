FROM mcr.microsoft.com/dotnet/sdk:6.0
WORKDIR /prog
RUN dotnet new console
COPY .cstemp Program.cs
CMD [ "dotnet", "run" ]