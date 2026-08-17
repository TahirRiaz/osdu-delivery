---
id: guide-chat-assistant
title: "The GUI chat assistant: the SQLFlow agent in the workbench, with per-user authority and voice input"
type: guide
summary: How the GUI's Assistant page answers questions - the same assistant core as the Slack bot, streamed over SSE from the control plane, with tool calls made under the signed-in user's own token, persisted conversations, image paste, and voice input via server-side transcription.
keywords:
  - chat
  - assistant
  - gui
  - streaming
  - sse
  - conversations
  - foundry
  - responses api
  - mcp
  - per-user
  - voice
  - dictation
  - transcription
  - image
  - screenshot
related:
  - guide-slack-assistant
  - guide-deployment
  - concept-authentication-and-identity
  - concept-control-plane
sourceRefs:
  - src/SqlFlow.Assistant/IAssistantGateway.cs
  - src/SqlFlow.Assistant/ResponsesApiGateway.cs
  - src/SqlFlow.Assistant/AnthropicGateway.cs
  - src/SqlFlow.Assistant/TranscriptionGateway.cs
  - src/SqlFlow.ControlPlane/Api/ChatEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - gui/src/features/chat/ChatPage.tsx
  - gui/src/features/chat/chatAdapters.ts
  - deploy/bicep/control-plane.bicep
  - deploy/bicep/ai-foundry.bicep
---

# The GUI chat assistant

The workbench's Assistant page (`/chat`) is a ChatGPT-style chat over the SQLFlow estate: ask "what failed last night?", "what feeds `dbo.Orders`?", or "why is this table short?", and the answer streams in, grounded in the live catalog and the reference docs through the same SQLFlow MCP server the Slack assistant uses. Paste a screenshot to ask about it, or press the microphone and ask out loud; the recording is transcribed server-side and lands in the composer as text.

## One assistant, two surfaces

The Slack bot and the GUI chat share one implementation: `SqlFlow.Assistant`, the provider gateways (Azure AI Foundry / OpenAI over the Responses API, Anthropic over the Messages API's MCP connector) and one instructions document. Only the surface differs: Slack answers are formatted as mrkdwn and posted once; GUI answers are GitHub-flavored Markdown streamed token by token, with live tool-call activity shown in the thread. Switching providers or updating the assistant's knowledge changes both surfaces at once.

## Per-user authority (the difference from Slack)

The Slack bot holds one shared read-scoped token, because everyone in a channel shares the bot's identity. The GUI chat has a signed-in user on every request, so the control plane forwards the caller's OWN bearer to the MCP server on every agent run: the assistant can read exactly what that user can read, and the control plane enforces the token's scopes per tool call exactly as it would for the user's own API calls. The tool allowlist still defaults to the read-only surface (`ControlPlane:Assistant:Mcp:AllowedTools`).

## The chain

```
GUI /chat -> control plane POST /api/v1/chat/ask (SSE stream)
          -> SqlFlow.Assistant gateway (Foundry / OpenAI / Anthropic)
          -> sqlflow-mcp over streamable HTTP (the tools, caller's bearer)
          -> control plane /api/v1 (bearer-scoped)
```

Conversations persist in the catalog (`ChatConversation` / `ChatMessage`): the transcript is the durable record, provider-side conversation state is only a cache of it, so a control-plane restart or a re-opened browser loses nothing. Deleting a conversation deletes its messages; conversations are strictly per-user.

## Voice input

Two modes, picked automatically:

- **Server-side transcription** (preferred): when a transcription deployment is configured (`ControlPlane:Assistant:Foundry:TranscriptionDeploymentName`, or `OpenAI:TranscriptionModel` in OpenAI mode), the mic records in the browser and `POST /api/v1/chat/transcribe` turns the recording into text via the provider's `/audio/transcriptions` endpoint, using the same managed identity / API key as the answers.
- **Browser speech recognition** (fallback): without a transcription model, the mic uses the browser's built-in speech recognition where available.

## Configuration

The feature is off by default and reports itself through `GET /api/v1/chat/capabilities`, so a GUI on an estate without AI simply explains that instead of erroring. Enable it with `ControlPlane:Assistant:*`:

- `Enabled=true`
- `Provider`: `AzureFoundry` (default, managed identity, no key), `OpenAI`, or `Anthropic`
- `Foundry:ProjectEndpoint` + `Foundry:ModelDeploymentName` (AzureFoundry mode)
- `Mcp:ServerUrl`: the deployed SQLFlow MCP endpoint
- optional: `Foundry:TranscriptionDeploymentName`, `MaxImages`, `MaxImageBytes`, `RunTimeoutSeconds`
- key-based modes: `OpenAI:ApiKey` / `Anthropic:ApiKey` accept `${env:...}`/`${keyvault:...}` references

In the Bicep estate this wires itself: once `aiFoundryName` + `aiFoundryModelName` and `mcpImage` are set (the same inputs the Slack assistant needs), `main.bicep` enables the chat on the control plane, points it at the estate's Foundry project and MCP server, and grants the control-plane identity the Cognitive Services OpenAI User role. Set `aiFoundryTranscriptionModelName` (for example `gpt-4o-mini-transcribe`) to deploy the audio model and light up server-side voice input.
