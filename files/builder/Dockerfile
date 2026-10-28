# The character builder: everything that turns your PSP and Saturn discs into the
# extra characters (sotn-decomp's asset tools need Go and Rust, the player code is
# built with a MIPS cross-compiler, the converters are Python with Pillow).
FROM rust:1.90-bookworm
COPY --from=golang:1.24-bookworm /usr/local/go /usr/local/go
ENV PATH=/usr/local/go/bin:$PATH GOPATH=/go
RUN apt-get update && apt-get install -y --no-install-recommends \
        gcc-mipsel-linux-gnu binutils-mipsel-linux-gnu make python3 python3-pil rsync \
    && rm -rf /var/lib/apt/lists/*
