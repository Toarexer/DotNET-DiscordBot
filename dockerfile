FROM mcr.microsoft.com/dotnet/sdk:6.0
WORKDIR /prog
RUN dotnet new console
RUN rm *.cs
ARG TARGETDIR
COPY .temp/${TARGETDIR}/ /prog/
CMD [ "dotnet", "run" ]