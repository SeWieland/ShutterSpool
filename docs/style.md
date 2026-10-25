# Code and documentation style

Prefer readable, intention-revealing code and explicit failure behavior.
Follow [.editorconfig](../.editorconfig), [Directory.Build.props](../Directory.Build.props), and pinned toolchains.
Use domain-accurate names and focused types. Comments explain constraints and decisions; warning suppressions need a reason.
Keep orchestration readable and extract substantive decisions when that improves understanding or testing.
Do not extract trivial guards or introduce indirection merely to satisfy a pattern.

## Dependencies and simplicity

Use [system boundaries](architecture/system-boundaries.md) as the component vocabulary.
Implement behavior vertically across its participating layers and Web UI, with feature folders only for real code.
Domain must not reference persistence, protocol, cache, preview or hosting packages.
Application owns use cases and ports; Infrastructure implements adapters; Server composes them.
HTTP DTOs stay beside endpoints and the frontend client is generated from OpenAPI.

Reuse existing code, platform features and installed dependencies before adding machinery.
Preserve opaque streamed-original references and honest provider-specific publication semantics.
Possible consumers alone do not justify generic repositories, base entities, event buses, plugin frameworks or empty projects.
Never cut validation, accessibility, error handling, security or media-safety checks to save code.
A deliberate shortcut comment names its known limit and upgrade trigger.

## UI and tests

Follow [UI guidance](ui.md), [PRODUCT.md](../PRODUCT.md), and the implemented [visual system](../DESIGN.md).
Use typed Vue components and composables for shared behavior, existing Nuxt UI/native controls and shared theme tokens.
Authorization and durable workflow decisions remain on Server.
Tests express observable behavior and realistic failures with independently derived expectations.
Avoid mirrored implementation assertions, test-count targets and speculative test frameworks.
The [test guide](../tests/README.md) distinguishes policy, persistence, architecture and browser evidence.

## Documentation ownership

| Information | Home |
| --- | --- |
| Purpose and durable design constraints | PRODUCT.md |
| Implemented visual system | DESIGN.md and .impeccable/design.json |
| User tasks | Using guides in the documentation index |
| Installation, maintenance and recovery | deploy guides and operator setup |
| Component behavior and runnable commands | Component README and source |
| Cross-component boundaries | Architecture and core-workflow guides |
| Contribution and agent conventions | CONTRIBUTING.md, AGENTS.md and focused style/UI guides |
| Published version/artifact/notes conventions | Release conventions |

Write for humans first, with bounded pages, short paragraphs and descriptive links.
Use steps for tasks and tables for real comparisons; keep Mermaid understandable in source.
Link canonical instructions instead of duplicating them and update the fact's owner when behavior changes.
Distinguish implemented capabilities from unavailable features, synthetic evidence from real hardware, and generated candidates from published releases.
Check local paths and anchors when changing navigation.
