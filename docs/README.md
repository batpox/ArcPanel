# ArcPanel Documentation

- [Architecture](Architecture.md) — ESP32 panels, .NET server, Home Assistant, and system boundaries.
- [Developer Guide](DeveloperGuide.md) — IDE versions, dependencies, setup, configuration, build, and coding conventions.
- [User Guide](UserGuide.md) — current ArcUnit screens, controls, and limitations.
- [Protocol](Protocol.md) — newline-delimited JSON message contracts.
- [Development Notes](Development.md) — existing setup instructions and conventions.
- [Project README](../README.md) — implementation history and configuration examples.
- [Images](images/README.md) — reserved location for screenshots and diagrams.

Documentation standards: record **IDE name and version** (and board/toolchain versions where applicable); use C# XML `<summary>` comments for public methods, `Task`-returning methods, and substantial asynchronous routines and Doxygen-style `@brief`, `@param`, `@return` comments for Arduino C++ functions. Update version details when the toolchain changes.
