# Changelog

All notable changes to this package are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows
[Semantic Versioning](https://semver.org/).

## 0.1.0 - Unreleased

### Added

- Read a quarter's account values, account text values and filing profiles as typed records, with filters
  on charter number, institution and account code.
- List the published quarters with their status and filing quorum.
- Download a quarter's files, and the index of every filing, as published.
- Check every file against the length and SHA-256 digest in the host's manifest before it is read.
- Keep verified files in a cache directory that uses the `quartercall` Python package's layout.
