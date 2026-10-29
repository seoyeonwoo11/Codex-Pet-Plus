The small app-cjs.asar, app-esm.asar, and app-root.asar test archives were generated with the official @electron/asar 4.3.1 packer. They contain our synthetic package.json, a main module exporting the number 42, an empty avatar renderer fixture, and (for the nested-entry fixtures) an unpacked native.node placeholder. They contain no files from ChatGPT or Codex.

app-no-version.asar is another synthetic archive generated with the same official packer, intentionally missing package.json.version to verify that an engine or install-folder version is never used as a substitute.
