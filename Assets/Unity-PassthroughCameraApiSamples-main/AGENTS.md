# Agent Instructions — Unity Passthrough Camera API Samples

A Unity sample project showing five scenes (`CameraViewer`, `CameraToWorld`, `BrightnessEstimation`, `MultiObjectDetection`, `ShaderSample`) that use MRUK's `PassthroughCameraAccess` component to read the Quest headset cameras.

## Source-of-truth files (read these first, do not duplicate their contents in this file)

For setup, build steps, SDK versions, and project layout, read:

- `README.md` — official setup, sample descriptions, troubleshooting, bug-report template
- `ProjectSettings/ProjectVersion.txt` — Unity editor version
- `Packages/manifest.json` — Unity package versions (MRUK, Unity Inference Engine)
- `Assets/PassthroughCameraApiSamples/` — all sample scenes, scripts, and the `StartScene` menu
- `LICENSE.txt` and `Assets/PassthroughCameraApiSamples/LICENSE.txt` — license terms

## Quest / Horizon-specific notes

- Requires **Quest 3 / 3S** — older Quest hardware does not expose passthrough camera frames.
- App needs the `horizonos.permission.HEADSET_CAMERA` permission and passthrough enabled in the project; do not strip these from the manifest.
- **XR Simulator does not support the Passthrough Camera API.** Test on a physical device or via Meta Horizon Link 2.1+; agents that try to verify in-editor will silently get nothing.
- `MultiObjectDetection` pulls in Unity Inference Engine (Sentis) and a YOLO model under MIT — keep that license marker if extracting scripts.
- Git LFS is used by this repo — run `git lfs install` before cloning.

# Meta Quest tooling

This is a Meta Quest / Horizon OS sample. The bespoke intro above is the source of truth for what this project is and how it's built — use it (and the files it points at) instead of restating facts from memory.

When the user asks anything about Quest device behavior, build / deploy / debug / capture flows, on-device performance, or Horizon OS APIs, reach for these tools instead of generic Unity answers:

- **`hzdb`** — Quest-aware ADB wrapper (device list, install / launch / stop, logs, screenshots, Perfetto traces, on-device docs search). Already wired up as an MCP server via `.mcp.json`, `.vscode/mcp.json`, and `.cursor/mcp.json`. Also runnable directly: `npx -y @meta-quest/hzdb <subcommand>`.
- **Meta Quest Agentic Tools** — the full skill set, including Unity-specific skills: <https://github.com/meta-quest/agentic-tools>. Install per your client (Claude Code: `/plugin install meta-vr@meta-quest`; Gemini CLI: `gemini extensions install https://github.com/meta-quest/agentic-tools`; Cursor / VS Code: install the **Meta Horizon** extension from the Marketplace).

A few behavior expectations:

- **Read this repo's files first.** Before answering anything project-specific, read `README.md` and whichever source-of-truth files the intro above points at. Don't restate their contents in chat — quote or link instead.
- **Use `hzdb` for device-side work.** Anything that touches an attached Quest (install, launch, logs, screenshot, capture, manifest inspection) goes through `hzdb`, not raw `adb`.
- **Check live Horizon OS docs before answering API questions.** `hzdb docs search "..."` queries the live docs; training data on Horizon OS APIs goes stale fast.
- **Don't fabricate SDK / engine versions.** If a version isn't visible in this repo's files, say so rather than guessing.
