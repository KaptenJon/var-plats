FROM postgres:18-bookworm@sha256:1757c628698dd1ca67ff7a5fc386bfd91040afc62a45484c346c788b0cbe5402
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-18-postgis-3 postgresql-18-postgis-3-scripts \
    && rm -rf /var/lib/apt/lists/*
