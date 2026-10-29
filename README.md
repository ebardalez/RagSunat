# SunatRAG

**English** | [Español](#español)

---

## English

Grounded Q&A over Peruvian tax regulation (SUNAT: *resoluciones de superintendencia*, *informes*, *normas legales*). Answers are meant to cite the source document and page. The corpus is scraped from [sunat.gob.pe](https://www.sunat.gob.pe).

This is a **learning project** for the Microsoft AI stack on .NET. It is educational and **not legal advice**.

### What it exercises

1. **Microsoft.Extensions.AI**: `IChatClient`, `IEmbeddingGenerator`, middleware (function invocation, OpenTelemetry, logging).
2. **Microsoft.Extensions.VectorData**: record model, vector store collections.
3. **Microsoft Agent Framework 1.x**: `AIAgent` / `ChatClientAgent`, sessions, context providers, skills, workflows.
4. **RAG end to end**: scraping, extraction, token-aware chunking, embeddings, hybrid retrieval, citations.

### Stack

- .NET 10, C# 14
- Blazor Web App, global InteractiveServer render mode, prerendering disabled
- Ollama on a LAN machine, accessed through OllamaSharp (chat `qwen3:8b`, embeddings `bge-m3`, 1024 dims). OllamaSharp types stay inside the composition root
- AngleSharp + Polly.Core (scraping), PdfPig (PDF text), Microsoft.ML.Tokenizers (chunking)
- OpenTelemetry, xUnit + NSubstitute
- Vector store: **SQLite** (planned, Phase 3). Azure AI Search was dropped from the original plan

### Solution layout

| Project | Purpose |
|---|---|
| `SunatRAG.Core` | Records, options, `AddSunatAi` composition root, health check, `CategoriaClassifier` |
| `SunatRAG.Ingestion` | Console app: `smoke`, `scrape`, `extract`, `inspect` commands |
| `SunatRAG.Web` | Blazor Web App (thin shell; chat and admin pages come later) |
| `SunatRAG.Tests` | xUnit tests |

### Status

| Phase | Scope | Status |
|---|---|---|
| 0 | Ollama connectivity, config, MEAI registration, health check, smoke test | Done |
| 1 | Polite scraper (robots.txt, 1 req/s, 429 stop, hash dedupe, manifest) | Done (2025 corpus: 434 PDFs) |
| 2 | PDF extraction, categorization, token-aware chunking | Code done; real bge-m3 tokenizer not yet verified |
| 3 | Vector record + SQLite store + resumable embedding ingestion | Next |
| 4 | Hybrid retrieval (dense + keyword) and reranking | Planned |
| 5 | Agent (tool or `AIContextProvider`), sessions, streaming, SUNAT skill | Planned |
| 6 | Evaluation and hardening | Planned |
| 7 | Workflows (stretch) | Planned |

Phase 2 result on the 2025 corpus (approximate tokenizer): 434 documents, 114 chunked (81 normative resolutions + 33 informes), 320 administrative resolutions excluded by default, 755 chunks, median ~414 tokens per chunk.

### Getting started

Prerequisites: .NET 10 SDK and an Ollama server reachable on your LAN with `qwen3:8b` and `bge-m3` pulled. Ollama has no authentication: keep it LAN-only, never expose it publicly. Set `OLLAMA_CONTEXT_LENGTH` (for example 16384) on the server.

1. Create the local config from the template (it is gitignored):

   ```powershell
   Copy-Item SunatRAG.Ingestion\appsettings.Development.json.example SunatRAG.Ingestion\appsettings.Development.json
   Copy-Item SunatRAG.Web\appsettings.Development.json.example SunatRAG.Web\appsettings.Development.json
   ```

   Then set `Ai:Ollama:Endpoint` (and optionally `Scraping:Contact`, appended to the User-Agent).

2. Build and test:

   ```powershell
   dotnet build SunatRAG.slnx
   dotnet test SunatRAG.slnx
   ```

3. Run from the repository root:

   ```powershell
   # Health check + one chat call + one embedding call
   dotnet run --project SunatRAG.Ingestion -- smoke

   # Scrape (flags: --from --to --limit --anexos --refresh-index)
   dotnet run --project SunatRAG.Ingestion -- scrape --from 2025 --to 2025

   # Extract and chunk (flags: --include-admin --approx-tokens --only <id> --limit N --target N --overlap N)
   dotnet run --project SunatRAG.Ingestion -- extract

   # Inspect one document (flags: --pages N --stream)
   dotnet run --project SunatRAG.Ingestion -- inspect <id>

   # Web app
   dotnet run --project SunatRAG.Web
   ```

   Exit codes for `scrape`: 0 ok, 1 bad arguments, 2 stopped by HTTP 429, 3 some downloads failed.

### Configuration

| Section | Keys |
|---|---|
| `Ai:Ollama` | `Endpoint`, `ChatModel`, `EmbeddingModel`, `EmbeddingDimensions` (must match the vector index), `RequestTimeout` (>= 5 min), `ContextLength` (token budgeting only) |
| `Scraping` | `FromYear`, `MinRequestInterval` (floor 1 s), `DataDirectory`, `IncludeAnexos`, `Contact` |
| `Chunking` | `TargetTokens` (500), `OverlapTokens` (80), `TokenizerModelPath`, `AllowApproximateTokenizer` |

The real chunking tokenizer is the bge-m3 SentencePiece model. Download `sentencepiece.bpe.model` from the BAAI/bge-m3 Hugging Face repository to `data/models/sentencepiece.bpe.model`, or use `--approx-tokens` for an estimate.

### Design notes

- **Provider swappable**: `AddSunatAi` registers `IChatClient` and `IEmbeddingGenerator` once with middleware; a `Provider` setting selects the implementation. Only `Ollama` exists today.
- **Polite scraping**: identifying User-Agent, robots.txt check on every run, minimum 1 s between requests, retry with Polly, stop on 429, content-addressed PDF cache.
- **Chunking**: heading-aware splitting (CONSIDERANDO, SE RESUELVE, Artículo, Título, Anexo), overlap only inside oversized sections. Each chunk has a stable `ChunkId` and a `TextHash` so Phase 3 can skip re-embedding.
- **Administrative resolutions** (plans, designations) are tagged by sumilla rules and excluded from indexing unless `--include-admin` is passed.

### Not committed

`data/` (PDFs, manifest, chunks), `appsettings.Development.json` and `HANDOFF.md` are gitignored on purpose.

### Limitations

- Scanned PDFs are flagged, not OCR'd (none found in the 2025 corpus).
- Only 2025 has been scraped and the classification rules were calibrated on it.
- Some corpus documents are draft resolutions (*proyectos*), not norms in force.
- Answers are informational and must always be checked against the cited source.

---

## Español

Preguntas y respuestas sobre normativa tributaria peruana (SUNAT: *resoluciones de superintendencia*, *informes*, *normas legales*) fundamentadas en documentos. Las respuestas deben citar el documento fuente y la página. El corpus se obtiene de [sunat.gob.pe](https://www.sunat.gob.pe).

Es un **proyecto de aprendizaje** del stack de IA de Microsoft sobre .NET. Es educativo y **no constituye asesoría legal**.

### Qué se practica

1. **Microsoft.Extensions.AI**: `IChatClient`, `IEmbeddingGenerator`, middleware (invocación de funciones, OpenTelemetry, logging).
2. **Microsoft.Extensions.VectorData**: modelo de registros y colecciones del almacén vectorial.
3. **Microsoft Agent Framework 1.x**: `AIAgent` / `ChatClientAgent`, sesiones, proveedores de contexto, skills, workflows.
4. **RAG de extremo a extremo**: scraping, extracción, chunking por tokens, embeddings, recuperación híbrida, citas.

### Stack

- .NET 10, C# 14
- Blazor Web App, modo de render global InteractiveServer, prerender deshabilitado
- Ollama en una máquina de la LAN, vía OllamaSharp (chat `qwen3:8b`, embeddings `bge-m3`, 1024 dimensiones). Los tipos de OllamaSharp quedan dentro de la raíz de composición
- AngleSharp + Polly.Core (scraping), PdfPig (texto de PDF), Microsoft.ML.Tokenizers (chunking)
- OpenTelemetry, xUnit + NSubstitute
- Almacén vectorial: **SQLite** (planificado, Fase 3). Azure AI Search se descartó del plan original

### Estructura de la solución

| Proyecto | Propósito |
|---|---|
| `SunatRAG.Core` | Records, opciones, raíz de composición `AddSunatAi`, health check, `CategoriaClassifier` |
| `SunatRAG.Ingestion` | Aplicación de consola: comandos `smoke`, `scrape`, `extract`, `inspect` |
| `SunatRAG.Web` | Blazor Web App (capa fina; las páginas de chat y administración vendrán después) |
| `SunatRAG.Tests` | Pruebas xUnit |

### Estado

| Fase | Alcance | Estado |
|---|---|---|
| 0 | Conectividad con Ollama, configuración, registro MEAI, health check, smoke test | Hecha |
| 1 | Scraper respetuoso (robots.txt, 1 req/s, parada en 429, dedupe por hash, manifiesto) | Hecha (corpus 2025: 434 PDFs) |
| 2 | Extracción de PDF, categorización, chunking por tokens | Código listo; falta verificar el tokenizador real de bge-m3 |
| 3 | Registro vectorial + almacén SQLite + ingesta de embeddings reanudable | Siguiente |
| 4 | Recuperación híbrida (densa + palabras clave) y reranking | Planificada |
| 5 | Agente (herramienta o `AIContextProvider`), sesiones, streaming, skill SUNAT | Planificada |
| 6 | Evaluación y endurecimiento | Planificada |
| 7 | Workflows (opcional) | Planificada |

Resultado de la Fase 2 sobre el corpus 2025 (tokenizador aproximado): 434 documentos, 114 con chunks (81 resoluciones normativas + 33 informes), 320 resoluciones administrativas excluidas por defecto, 755 chunks, mediana de ~414 tokens por chunk.

### Primeros pasos

Requisitos: SDK de .NET 10 y un servidor Ollama accesible en tu LAN con `qwen3:8b` y `bge-m3` descargados. Ollama no tiene autenticación: manténlo solo en LAN y nunca lo expongas públicamente. Configura `OLLAMA_CONTEXT_LENGTH` (por ejemplo 16384) en el servidor.

1. Crea la configuración local a partir de la plantilla (está en `.gitignore`):

   ```powershell
   Copy-Item SunatRAG.Ingestion\appsettings.Development.json.example SunatRAG.Ingestion\appsettings.Development.json
   Copy-Item SunatRAG.Web\appsettings.Development.json.example SunatRAG.Web\appsettings.Development.json
   ```

   Luego define `Ai:Ollama:Endpoint` (y opcionalmente `Scraping:Contact`, que se agrega al User-Agent).

2. Compila y prueba:

   ```powershell
   dotnet build SunatRAG.slnx
   dotnet test SunatRAG.slnx
   ```

3. Ejecuta desde la raíz del repositorio:

   ```powershell
   # Health check + una llamada de chat + una de embedding
   dotnet run --project SunatRAG.Ingestion -- smoke

   # Scraping (opciones: --from --to --limit --anexos --refresh-index)
   dotnet run --project SunatRAG.Ingestion -- scrape --from 2025 --to 2025

   # Extraer y generar chunks (opciones: --include-admin --approx-tokens --only <id> --limit N --target N --overlap N)
   dotnet run --project SunatRAG.Ingestion -- extract

   # Inspeccionar un documento (opciones: --pages N --stream)
   dotnet run --project SunatRAG.Ingestion -- inspect <id>

   # Aplicación web
   dotnet run --project SunatRAG.Web
   ```

   Códigos de salida de `scrape`: 0 correcto, 1 argumentos inválidos, 2 detenido por HTTP 429, 3 algunas descargas fallaron.

### Configuración

| Sección | Claves |
|---|---|
| `Ai:Ollama` | `Endpoint`, `ChatModel`, `EmbeddingModel`, `EmbeddingDimensions` (debe coincidir con el índice vectorial), `RequestTimeout` (>= 5 min), `ContextLength` (solo para presupuesto de tokens) |
| `Scraping` | `FromYear`, `MinRequestInterval` (mínimo 1 s), `DataDirectory`, `IncludeAnexos`, `Contact` |
| `Chunking` | `TargetTokens` (500), `OverlapTokens` (80), `TokenizerModelPath`, `AllowApproximateTokenizer` |

El tokenizador real para el chunking es el modelo SentencePiece de bge-m3. Descarga `sentencepiece.bpe.model` del repositorio BAAI/bge-m3 en Hugging Face a `data/models/sentencepiece.bpe.model`, o usa `--approx-tokens` para una estimación.

### Notas de diseño

- **Proveedor intercambiable**: `AddSunatAi` registra una sola vez `IChatClient` e `IEmbeddingGenerator` con middleware; la opción `Provider` elige la implementación. Hoy solo existe `Ollama`.
- **Scraping respetuoso**: User-Agent identificable, revisión de robots.txt en cada ejecución, mínimo 1 s entre solicitudes, reintentos con Polly, parada ante 429 y caché de PDFs direccionada por contenido.
- **Chunking**: división consciente de encabezados (CONSIDERANDO, SE RESUELVE, Artículo, Título, Anexo), con solapamiento solo dentro de secciones demasiado grandes. Cada chunk tiene un `ChunkId` estable y un `TextHash` para que la Fase 3 evite recalcular embeddings.
- **Resoluciones administrativas** (planes, designaciones) se etiquetan por reglas sobre la sumilla y se excluyen del índice salvo que uses `--include-admin`.

### Fuera del repositorio

`data/` (PDFs, manifiesto, chunks), `appsettings.Development.json` y `HANDOFF.md` están en `.gitignore` a propósito.

### Limitaciones

- Los PDFs escaneados se marcan, no se les aplica OCR (no apareció ninguno en el corpus 2025).
- Solo se ha descargado 2025 y las reglas de clasificación se calibraron con ese año.
- Parte del corpus son proyectos de resolución, no normas vigentes.
- Las respuestas son informativas y siempre deben contrastarse con la fuente citada.
