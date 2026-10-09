# native/sqlite-vec

Vendored loadable SQLite extensions for the `sqlite-vec` vector-search layer.

- Version: see `VERSION` (pinned to v0.1.9).
- Source: https://github.com/asg017/sqlite-vec/releases/tag/v0.1.9
- License: MIT / Apache-2.0 (dual-licensed by the sqlite-vec project).

Layout: `native/sqlite-vec/<rid>/<library>`

| RID       | Library    | Release asset                                            |
|-----------|------------|----------------------------------------------------------|
| win-x64   | `vec0.dll` | `sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz`        |
| linux-x64 | `vec0.so`  | `sqlite-vec-0.1.9-loadable-linux-x86_64.tar.gz`          |
| osx-arm64 | `vec0.dylib` | `sqlite-vec-0.1.9-loadable-macos-aarch64.tar.gz`       |

The app resolves the path from `ECONDATA_SQLITE_VEC_PATH`, defaulting to the
current platform's RID directory. Only `win-x64` is vendored in this repo today;
add other RID directories from the matching release asset when needed.