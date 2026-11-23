using Karaokay.Importer.Commands;

// Karaokay.Importer — CLI that ingests extracted Rock Band folders into the canonical
// three-file layout (instrumental.opus, vocals.opus, lyrics.json). Dispatch lives in
// ImporterCli so every command remains unit-testable.
return await ImporterCli.RunAsync(args).ConfigureAwait(false);
