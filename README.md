# TCA Multiplayer Mod

A multiplayer mod for Tiny Combat Arena (v0.14.1.4) that enables 2-player PvP dogfighting with direct UDP networking.

## Features
- **Direct UDP Networking:** Fast, low-latency state synchronization (128Hz).
- **Shooter Authority:** Reliable hit detection.
- **Visual Synchronization:** Syncs aircraft position, rotation, control surfaces, gear, flaps, afterburners, and muzzle flashes.
- **Combat Systems:** Functional missiles, radar locks, and damage synchronization.

## Installation
1. Download the latest release.
2. Extract the `TCAMultiplayer.dll` into your game's `BepInEx/plugins` folder.
3. Launch the game.
4. Press **F8** to open the Multiplayer Menu.

## Development
See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for detailed architecture and roadmap.

### Build Instructions
1. Copy the required game DLLs to the `libs/` folder.
2. Open `TCAMultiplayer.sln` in Visual Studio.
3. Build for Release.

## Automated Issue Fixing (mini-swe-agent)

This repository uses [mini-swe-agent](https://github.com/SWE-agent/mini-swe-agent) to automatically analyze issues and open pull requests with code fixes.

### How to Trigger

Post a comment containing `/mini-swe-fix` on any issue. The workflow will:

1. Analyze the issue context.
2. Run mini-swe-agent to produce a code fix.
3. Commit the changes to a new branch (`mini-swe-fix/issue-<number>`).
4. Open a **ready-for-review** pull request targeting `main`.

> **Note:** PRs are never auto-merged. All changes require human review before merging.

### Required Secrets

| Secret | Description |
|---|---|
| `LLM_API_KEY` | Anthropic API key used by mini-swe-agent. |
| `LLM_BASE_URL` | *(Optional)* Custom Anthropic-compatible base URL/endpoint. Leave unset to use the default Anthropic API. |
| `PAT_TOKEN` | Personal access token with `repo` scope, used to push branches and open PRs. |
| `PAT_USERNAME` | GitHub username associated with `PAT_TOKEN`, used for git commit authorship. |

### Anthropic / Custom Endpoint Configuration

mini-swe-agent is configured to use the Anthropic provider (`anthropic/claude-opus-4-6`).
The `LLM_BASE_URL` secret is mapped to `ANTHROPIC_BASE_URL`, allowing a custom Anthropic-compatible endpoint to be used without any code changes.

No OpenAI credentials are required.

### Troubleshooting

- If the workflow does not trigger, ensure the comment is on an **issue** (not a pull request) and contains `/mini-swe-fix` exactly.
- If the agent produces no changes, the workflow exits without opening a PR.
- Check the [Actions tab](../../actions) for detailed run logs.

## License
[License Information Here]

