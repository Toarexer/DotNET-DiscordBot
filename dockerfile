FROM mcr.microsoft.com/dotnet/sdk:6.0
WORKDIR /prog
RUN dotnet new console
RUN rm *.cs
COPY .cstemp/ /prog/
CMD [ "dotnet", "run" ]