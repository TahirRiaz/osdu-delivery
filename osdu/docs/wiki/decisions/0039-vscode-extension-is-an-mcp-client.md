---
id: delivery-wiki-0039-vscode-extension-is-an-mcp-client
title: "Decision 0039: the VS Code extension is a client of the MCP server, and a browser sign-in goes through the GUI"
type: decision
summary: "Why the extension reads the estate through the MCP server's tools, shares its sign-in, and approves a browser sign-in in the GUI, where Microsoft sign-in lives."
keywords:
  - vs code extension
  - mcp client
  - single sign-on
  - microsoft entra
  - device grant
  - device approval page
  - template folders
  - extension host
updated: 2026-10-30
sourceRefs:
  - sqlflow/tools/sqlflow-vscode/src/host.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/mcpServer.ts
  - sqlflow/tools/sqlflow-vscode/src/mcp/signIn.ts
  - sqlflow/gui/src/auth/DeviceApprovalPage.tsx
  - sqlflow/src/SqlFlow.ControlPlane/Security/DeviceApprovalPage.cs
  - osdu/hosts/osdu-delivery-vscode/src/extension.ts
  - osdu/hosts/osdu-delivery-vscode/src/templates.ts
referenceRefs:
  - delivery-guide-vscode
  - delivery-guide-mcp
  - delivery-concept-authentication-and-identity
---
# 0039: The VS Code extension is a client of the MCP server, and a browser sign-in goes through the GUI

Status: accepted. Documented in [the VS Code extension](../../reference/guides/vscode.md).

The human's words: "The goal for this extion is to provide all required information to develop pipelines. Note that the
combination with MCP should be built in.", "I want the vscode to be the primary tool for yaml file development. It should
have access all relevant infomration.", and "I want Singel sign on option where we authenticate with the browser", "azure
ms signon is the main feature". The extension had not moved since SQLFlow was vendored: it keyed on `*.flow.yaml`, which
this product's repositories do not use, so it never activated on one, and it knew nothing of the module's kinds.

## The extension reads through the server

Every view, panel and command reads through the MCP server's tools, and the extension holds no REST client.

- **One estate, one set of rules.** The assistant beside the code and the panel it answers about read the same answers,
  under the same rule of what is metadata ([the server's own decision](../../../../sqlflow/docs/wiki/decisions/the-mcp-server-is-the-assistants-one-definition.md)).
- **A missing tool is a gap in the server, and is filled there.** The first pass over what a developer needs found seven:
  a run's trace, which `/trace/text` had been built for and no tool read; firing a schedule and syncing a repository after
  a push, which the old extension did over REST; the datasources a `connection` can name; the kinds, their run operations
  and the keys of a document that is not a flow (the key tools took only a flow kind, though the guide said otherwise); a
  flow's issues, inventories and removals; and a dimension's blueprint. An assistant gains each as the extension does.
- **Rejected: a REST client for what the tools withhold.** A record's rendered content, what OSDU holds and a dimension's
  values stay out of the views. The extension signs in through the server, so a second path would need a second
  credential. `sqlflow preview`, `check` and `values` answer those on the working copy, which is what a developer wants
  from them anyway, and the GUI answers for what is synced.

## One sign-in, in the browser, through the GUI

The server already kept a session and renewed it, so the extension uses it: signing in is the server's `login`, and an
assistant registered with the same program shares the session. What it did not have was Microsoft. Its approval page,
`GET /device`, takes a username and a password, and an account that signs in with Entra has neither, so a device grant
could not reach one, in the extension or in any assistant.

The GUI has Entra: its origin is the redirect URI an app registration names, and `POST /auth/device/approve` already takes
any signed-in session and carries over its `auth_time`, so the client's session keeps the approving person's limit. So the
GUI got a `/device` page behind its sign-in, and the control plane hands `GET /device` to it when it knows where the GUI
is, from `ControlPlane:Notifications:GuiBaseUrl` or the one CORS origin a deployment allows. Every client of the device
grant gains single sign-on, the MCP server's `login` in Claude Code included, with nothing more to register.

- **Rejected: VS Code's built-in Microsoft account provider.** One click where the account is already in the editor, but
  it needs the app registration to take public-client redirect URIs a deployment does not have today, which an estate's
  directory owners may not grant, and it would serve this one client alone. It reads an ID token VS Code does not document
  as part of its API.
- **Rejected: a page of the control plane's own that signs in with Entra.** Its origin would need registering as a second
  redirect URI, on every deployment.
- **Rejected: approving by opening the link.** One click fewer, and anyone who sends a link could then sign in as whoever
  opens it, which is device-code phishing. Approving takes a click on a code the person can compare.

## The extension is a host, as the server and the GUI are

`sqlflow/tools/sqlflow-vscode` became a library whose own extension is the host with no modules, and
`osdu/hosts/osdu-delivery-vscode` composes it with the delivery module, compiling its sources in place as the GUI does.
A module adds sections to the Document view, references, lenses, commands and a view; the host names the program, the
census, the documentation and the skeletons. VS Code reads a manifest as a file, so this one restates SQLFlow's
contributions, and a suite holds the two together.

## Mappings read the working copy

A mapping's completion, hover and findings read the template folders on its path from disk rather than asking the control
plane, as `sqlflow template folders` does: the template a mapping is being written against is often not pushed yet, and a
person types faster than a round trip. The control plane's check stays the final word, and the findings as you type say
only what is certain: a key the template does not hold, and a required property nothing fills.

- **Rejected: the language server knowing templates.** It is SQLFlow's and sees one file; a template is a module's, and
  reading a folder is not what a language engine shared with the GUI's WebAssembly build can do.
