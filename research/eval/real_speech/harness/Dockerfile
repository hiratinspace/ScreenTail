# ST-031: where measure.py runs the harness. whisper.cpp's Linux build needs the OpenMP runtime, which
# the .NET runtime image does not carry. Nothing of ScreenTrail is copied in: the built harness and the
# recordings are mounted when it runs.
FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*
