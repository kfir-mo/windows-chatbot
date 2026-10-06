# Search and token-efficiency validation — 2026-09-30

## Verified changes

- Immutable local inverted index with term-frequency/rarity ranking, filename and heading weights, Hebrew vowel-mark normalization and conservative prefix variants. No embeddings, external search service or AI calls for indexing/search.
- Incremental refresh reuses both extracted text and prepared token frequencies for unchanged passages. A cancelled refresh retains the previous snapshot. Cache identity includes extractor version, chunk size and subfolder settings.
- Search ranks the whole library; permission and freshness checks still gate returned passages. Up to three candidate passages per file prevent one long file from monopolizing ordinary search. Selecting a document supports focused search within it or sampled overview excerpts across its length.
- Relevant verbatim excerpts, exact duplicate/containment removal, 4,800 evidence-character budget and 2,000 prior-context-character budget. Different dates/numbers are not discarded as near-duplicates. Current question is not repeated in history input.
- Exact greetings and simple library listings use no AI. Simple source selections skip AI planning. Automatic AI summarization of old messages was removed; saved history remains intact. Document answers still use a separate AI review pass. General direct AI replies are also reviewed.

## Reproducible 500-file benchmark

Command: dotnet run --project tools/IndexBenchmark
Raw output: validation/index-benchmark.json

Synthetic corpus: 500 DOCX documents with Hebrew/English topics, repeated boilerplate, unique project identifiers and answer codes near document ends. 500 queries check one expected document each. This measures lexical/identifier retrieval; it is not a general semantic-recall guarantee.

Latest measured run on this PC, local disk:

| Check | Result |
| --- | --- |
| Expected document ranked first | 500 / 500 |
| Required answer fact present in excerpts | 500 / 500 |
| Initial extraction/index build | 3,086 ms |
| Unchanged refresh | 46 ms; 500 reused, 0 extracted |
| One changed + one deleted file | 389 ms; 498 reused, 1 extracted |
| Search median / maximum | 2.95 ms / 24.84 ms |
| Mean returned evidence | 2,735 characters |
| Previous full-scan algorithm, 20 baseline queries | 396 ms median; 7,828 mean evidence characters |
| AI calls for indexing/search | 0 |

Baseline uses the previous exact-word overlap scan and 8-passage selection on the same corpus. It measures ranking work without permission I/O, so it is not a network benchmark. New measurements include permission/freshness checks. Timings vary by machine, cache and document complexity. The new evidence ceiling is 4,800 characters versus the previous default ceiling of 14,400 (67% lower). Character reduction is not an exact token or cost reduction.

## Automated and live checks

46 automated tests pass, including ranking rare terms, Hebrew normalization, answers at document ends, budget enforcement, preserving different numeric evidence, bounded valid source context, zero-call greetings, selected-source reading, cancelled refresh, index changes/deletions, ACL revocation, locked files, API redirects and response validation. WPF smoke checks and sample DOCX extraction/search pass.

Live OpenAI checks used only the fictional sample documents and fresh in-memory conversations:

| Request | Calls | Reported input / output tokens |
| --- | --- | --- |
| Hebrew greeting | 0 | 0 / 0 |
| Password reset | 3 (plan, answer, review) | 1,344 / 199 |
| List library | 0 | 0 / 0 |
| Read first listed document | 2 (answer, review) | 1,941 / 240 |

Password reset correctly included extension 4321 and 60-minute validity. The first-source follow-up explained the leave-policy document. Token usage is from that live test's API usage fields; later requests and other models vary. No personal chat history or internal documents were used for the live test.

## Destination checks still required

Real SMB latency, actual 500-file content quality, private-model query rewriting, scanned/complex PDFs and organization-specific synonyms must be tested on the closed network. Keep the internal endpoint configuration from DEPLOYMENT.md. Lexical retrieval plus query rewriting cannot guarantee every semantic paraphrase. Long document overviews are sampled, not a claim that every page was sent to AI.
