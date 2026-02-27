# Patterns and Methodologies for Building Complex Multimodal Applications on Reachy Mini with .NET

## Reachy Mini constraints and the platform architecture you can lean on

Reachy Mini is developed collaboratively by entity["company","Hugging Face","ai platform company"] and entity["company","Pollen Robotics","robotics company france"], and it is intentionally designed around a “thin hardware layer + networked application layer” model: the device-side software focuses on hardware I/O and safety, while higher-level application logic can run anywhere you can reach the robot over a network. citeturn6view2turn4search4turn4search20

Two hardware/software “shapes” matter for architecture decisions:

- **Wireless Reachy Mini** uses a **Raspberry Pi Compute Module 4** and, per the official datasheet, the controller board includes a **CM4104016 variant (Wi‑Fi, 4GB RAM, 16GB flash)** and a **USB‑C output** that “one can plug a device such as a usb key” into (but the robot **will not charge through this USB port**). citeturn2view0turn5search0  
- **Reachy Mini Lite** is effectively a peripheral: it connects to a host computer over **USB‑C for control** and similarly does **not** charge through that USB port. citeturn1view0

image_group{"layout":"carousel","aspect_ratio":"16:9","query":["Reachy Mini robot on desk","Reachy Mini back interface USB-C port","Reachy Mini Lite robot connected to laptop"],"num_per_query":1}

On the software side, Reachy Mini’s documentation describes a **client–server architecture**:

- The **Daemon (server)**: runs on the computer attached to the robot (Lite), or on the embedded computer (Wireless), handles **hardware I/O (USB/Serial), safety checks, and sensor reading**, and exposes **REST + WebSocket** endpoints. citeturn6view2turn11view2turn11view3  
- The **SDK (client)**: connects to the daemon over the network, and the docs explicitly call out the architectural advantage: **you can run AI code on a powerful server while the daemon runs on the Raspberry Pi**. citeturn6view2

This is the first “scalability” answer: **you don’t have to scale complexity on-device** if you treat the robot as a networked hardware endpoint with a safety envelope (daemon), and treat “AI + orchestration” as an external service that can evolve and scale independently. citeturn6view2turn11view3

Media is also explicitly architected as a shared platform service. For Wireless:

- Audio/video streams are accessible locally (embedded apps) or remotely (Python SDK and web clients) via **WebRTC**, and Reachy Mini uses **GStreamer** for media handling. citeturn6view0turn6view1  
- The daemon manages streams so **multiple applications can access them simultaneously**; video is shared between a Unix socket and a WebRTC server; audio is configured (via `.asoundrc`) under specific device names so multiple apps can access it. citeturn6view0  

For Lite, there is a specific gotcha the docs call out: a default backend uses OpenCV + sounddevice, and **sounddevice can lock the audio card**—which can break scenarios where you need autonomous sound plus dashboard-triggered motions. citeturn6view0

Finally, note that Reachy Mini’s “App” model implies **resource exclusivity** at the behavior layer: when an App is running, it “takes control of the robot.” citeturn1view1  
Even if you build a .NET controller, you should assume you need **resource arbitration** (who currently “owns” motion, mic, camera, audio output), because the platform is built around controlled ownership. citeturn1view1turn6view2

## A reference architecture that keeps multimodal complexity manageable

A maintainable Reachy Mini system typically becomes a *small distributed system* with clear boundaries:

- **Device platform boundary**: daemon + low-level media/motor/sensor drivers (already provided by Reachy Mini). citeturn6view2turn6view0  
- **Application boundary**: your orchestration, skills/tools, memory/RAG, policies, and user experience loop—ideally in processes you can update frequently, test in isolation, and restart safely.

This is consistent with Reachy Mini’s own public “conversation app” reference, which describes a layered architecture connecting user, AI services, and robot hardware, plus an “async tool dispatch” layer that integrates motion, camera capture, and (optional) head tracking. citeturn12view0turn12view1

A practical, scalable decomposition for your .NET codebase is:

- **Adapters** (outside): robot transport (HTTP/WebSocket), OpenAI Realtime transport (WebRTC/WebSocket), storage/DB, telemetry. (The daemon’s REST + WebSocket endpoints are explicitly designed for non-Python controllers.) citeturn11view3turn11view2turn6view2  
- **Perception pipelines** (middle): audio ingestion, wake-word + VAD, ASR/transcription, camera frame sampling, vision analysis, etc. Reachy Mini’s own example app treats camera capture as a tool that grabs the latest frame and routes it to the realtime model for analysis. citeturn12view1turn6view0turn6view5  
- **Interaction core** (inside): conversation state machine, turn-taking/interruption policy, tool/skill router, safety policy, and “behavior compositor” (how speech, gaze/head tracking, and gestures blend). Reachy Mini’s example app describes a layered motion system where primary moves are queued while speech-reactive wobble/head-tracking is blended. citeturn12view0  
- **Skills/tools** (inside, plugin boundary): a stable interface with declarative metadata (“requires camera”, “requires motion”, “idempotent”, “timeout=…”) and a runtime that can safely execute, cancel, and report tool outputs.

This maps well to the tool-calling mental model in OpenAI’s function calling guide (tools are explicit functions with structured arguments; you return tool outputs tied to tool call IDs). citeturn9search1turn9search14  
It also aligns with OpenAI’s Realtime server-side controls guidance: **tool use and business logic typically belong on your application server** to keep logic private and client-agnostic. citeturn9search2

A key maintainability pattern for “skills/tools” is to treat them as **versioned plugins** with strict boundaries. Reachy Mini’s conversation app demonstrates this idea concretely via “profiles” that define instructions and enabled tools (`tools.txt`), with resolution rules that prefer profile-local tools before core tools, and support for external tool directories. citeturn12view2  
The need you described—“tools, skills, etc.” without an unmanageable codebase—is exactly what plugin boundaries + declarative enablement are for.

## Concurrency and streaming patterns in .NET that fit a quad-core device

### What the hardware implies (and what it does not)

Wireless Reachy Mini’s Compute Module 4 configuration implies a **quad-core CPU** and constrained resources. The official CM4 product brief describes the processor as a **Broadcom BCM2711 quad-core Cortex‑A72 (ARMv8) 64-bit SoC @ 1.5GHz**, with RAM depending on variant; Reachy Mini’s controller board is documented as a 4GB model. citeturn5search0turn2view0  
That is enough concurrency for multiple pipelines (audio ingest, networking, robot control, vision sampling), but it is not “desktop class” for heavy local inference, high-res vision, or large vector stores.

### The maintainable “shape” in .NET: host + supervised workers

For long-running device applications, a reliable baseline in .NET is the **Generic Host** with **Hosted Services / Worker Services**: it gives you standardized startup/lifetime management plus dependency injection, logging, and configuration. citeturn7search4turn7search0turn8search26  
On a robot, that translates into a set of **supervised, restartable background loops**:

- Audio input pipeline worker  
- Audio output playback worker  
- Camera sampling worker  
- Realtime session worker (OpenAI transport + event handling)  
- Robot control worker (state subscribe + command execution)  
- Memory/RAG worker (remote retrieval + caching)  

This is highly scalable from a *codebase* standpoint because each worker has a narrow responsibility, clear inputs/outputs, and can be tested with fake adapters.

### Bounded queues and backpressure are non-negotiable for multimodal work

The most common failure mode in multimodal device apps is *unbounded accumulation*: audio frames pile up, camera frames accumulate, network retries explode, and eventually you get latency balloons and GC pressure.

In .NET, two primitives are particularly well suited for this:

- **System.Threading.Channels**: async producer–consumer data structures for passing data between producers and consumers asynchronously. citeturn7search1turn7search5  
- **TPL Dataflow**: message passing building blocks designed for high-throughput, low-latency pipelines, with explicit control over buffering and flow. citeturn7search2turn7search26  

For device apps, the architectural principle is: **every “stream” is a pipeline of bounded stages**.

Examples (conceptual):

- mic → (wake word) → (VAD) → encode → OpenAI transport  
- camera → downsample → “should we snapshot?” → JPEG → OpenAI transport  
- OpenAI events → dialogue state machine → tool router → robot command queue  
- robot state stream → composer → posture/head tracking offsets → command queue  

The reason this stays maintainable is that a pipeline architecture forces you to define boundaries and “contracts” between stages; TPL Dataflow is explicitly oriented around composing blocks into pipelines. citeturn7search2turn7search10

### Performance hygiene that matters on a small device

On constrained devices, micro-allocations and buffer copying become macro-problems because they trigger GC and steal CPU from real-time loops.

Microsoft’s performance guidance for high-throughput server apps maps surprisingly well to device streaming workloads:

- Pool large buffers with `ArrayPool<T>` and avoid frequent large allocations on hot paths. citeturn7search7turn8search19  
- Prefer span-based parsing/processing where possible: `Span<T>` is designed to represent contiguous memory with performance characteristics like arrays. citeturn7search15  
- If you’re moving bytes around (audio chunks, encoded frames), consider the “pipelines” abstractions that are designed around multi-buffer data and backpressure (`System.IO.Pipelines`, `System.Buffers`). citeturn7search23turn7search11

Cancellation is also an architectural tool: cancellation tokens let you tear down and restart streaming subsystems cleanly. Task cancellation in .NET is cooperative and designed around timely termination after `Cancel()` is signaled. citeturn8search1turn8search5  
Even API shape matters for maintainability: Microsoft’s guidance explicitly treats “CancellationToken last” as a best practice because tokens tend to flow through many call layers. citeturn8search8

## Multimodal interaction design: wake word, continuous audio/video, turn-based chat, and “barge-in”

### Align your interaction model to the transports you’re actually using

Reachy Mini Wireless already exposes media streams in a way that expects concurrent use and remote access:

- Audio/video are accessible remotely through **WebRTC**, and GStreamer is part of the media handling story. citeturn6view0turn6view1  
- Streams are managed by the daemon explicitly so multiple applications can access them. citeturn6view0  

On the OpenAI side, the Realtime API is designed for **low-latency multimodal inputs/outputs** (audio, images, text) and is explicitly described as stateful “Realtime Sessions” with a conversation and model-generated responses. citeturn6view5turn6view6  

OpenAI’s WebRTC guide recommends **WebRTC rather than WebSockets for more consistent performance** when connecting from client-side environments, while noting WebRTC is lower-level than their higher-level SDKs. citeturn6view4turn6view5  
In practice, for a .NET robot application you have two common patterns:

- **Device/server uses WebSockets to OpenAI**, and you manage audio buffers manually (more engineering, but easier than implementing full WebRTC in .NET). The Realtime conversations guide notes that when using WebSockets, you manually send base64 audio into the input audio buffer events. citeturn6view6  
- **A browser or dedicated gateway uses WebRTC to OpenAI**, and your .NET service orchestrates tools/skills and state (separating media-plane complexity from robot control).

Either can be maintainable—what hurts maintainability is mixing both styles without a clean boundary.

### Treat “wake word” and “continuous conversation” as distinct but connected loops

You described merging trigger-word and continuous audio. The mistake that tends to create an unmanageable codebase is to make one giant “audio manager” that tries to do everything. A more scalable approach is:

- **Wake-word gate loop**: always-on, minimal CPU, produces a single event: `WakeWordDetected`.  
- **Conversation loop**: a state machine that runs only when gated open, and is allowed to be “heavy” (streaming, transcription, turn-taking, interruption).

Reachy Mini’s own reference implementations lean on **VAD** as a core building block (the official integrations page points to a conversation demo combining VAD + LLMs + TTS). citeturn11view3  
So, even if you add a wake word, it’s typically “wake word → open VAD-governed session.”

### Barge-in (user interruption) must be first-class

If you do full-duplex audio (user can speak while the robot speaks), your architecture must support barge-in cleanly. The OpenAI Realtime conversations guide explicitly describes an interruption flow where the client monitors for `input_audio_buffer.speech_started` events, stops current playback, and truncates/removes the unplayed portion of the model’s last response from the conversation. citeturn6view6  

This is a maintainability point: **interruption is not a corner case**—it’s a core state transition. Your dialogue manager should explicitly model this (e.g., `Speaking → Interrupted → Listening`), and your audio output worker should be controllable (stop/flush/cancel). The Realtime API reference also contains explicit events for clearing output audio buffers (intended for WebRTC/SIP) and describes cancel/clear sequencing. citeturn0search8turn9search3  

### “Turn-based chat with snapshot images” fits best as a sampled vision pipeline

Instead of attempting to stream every frame into the model (which will overload bandwidth/cost/latency), treat vision as:

- **Continuous local capture**
- **Periodic or event-triggered snapshots** (e.g., every N seconds, or when motion stops, or on user prompt)
- **A tool call** that captures “latest frame” and sends it for analysis

This is exactly how the Reachy Mini conversation app exposes vision: a `camera` tool “captures the latest camera frame and send[s] it to gpt‑realtime for vision analysis,” and vision can also be switched to a local model via a flag. citeturn12view1turn12view0  

This pattern scales maintainably because the vision pipeline becomes a service with a single stable interface: “give me a snapshot,” not “subscribe to a firehose.”

### Session lifecycle and context growth are architectural concerns

OpenAI’s Realtime sessions have lifecycle constraints that need to influence your architecture:

- The Realtime conversations guide states sessions are stateful, and the **maximum duration is 60 minutes**. citeturn6view6turn9search0  
- The same guide notes some session properties (like `voice`) can’t be updated after audio output has occurred once. citeturn6view6  
- OpenAI’s developer notes describe token-window and truncation behavior for `gpt-realtime` sessions and constraints on session instructions/tools length. citeturn9search5  
- OpenAI also publishes a cookbook example focused on **context summarization** for Realtime to prevent quality drift as conversations grow. citeturn9search13  

For maintainable device code, treat these as product requirements:
- you will need **session rotation**
- you will need **summarization + memory persistence**
- you will need **recoverable reconnection semantics**

## Storage expansion, networking, and RAG with ~16GB flash and real-world connectivity

### What’s actually on-device, and how “extendable storage” really is

The Reachy Mini Wireless controller board is documented as using a CM4 module with **16GB flash**. citeturn2view0  
Raspberry Pi’s own CM4 product brief confirms CM4 variants have optional onboard eMMC (8/16/32GB), which is consistent with the Reachy Mini configuration. citeturn5search0  

So a “~2GB free” observation is plausible once you account for OS + packages + logs + caches, but the exact free space is configuration-dependent.

On “is it extendable via USB‑C?”: the Reachy Mini hardware datasheet is explicit that the CM4 controller board provides **USB‑C output** and you can “plug a device such as a usb key,” with the important caveat that the robot **will not charge through this USB port**. citeturn2view0  

That supports a practical interpretation: **yes, you can attach external USB storage**, but you should treat it as *peripheral storage* (with real embedded gotchas), not automatically as “internal flash expansion.”

Two reliability considerations follow from the same sources:

- If the USB‑C port does not provide charging/power in the expected way, some drives may require their own power or a powered hub. The Raspberry Pi community commonly recommends powered USB hubs to avoid power instability with external storage. citeturn2view0turn5search9  
- Your application should assume hot-unplug risk and filesystem corruption risk, and keep anything essential (config, keys, minimal state) on the internal flash.

### Networking boundaries: robot control vs media vs cloud AI

Reachy Mini explicitly supports “non-Python controllers” via the daemon’s REST and WebSocket API (docs at `http://localhost:8000/docs`, and a “full state” endpoint + state WebSocket are called out). citeturn11view3turn11view2  
This is the cleanest integration point for .NET: treat the daemon as your hardware gateway and build typed clients around:

- **Robot state subscription (WebSocket)** → local model of robot state  
- **Command publishing (REST)** → motion/audio control, etc.  
- **Health/availability** → used by your supervisor to restart workers or degrade gracefully

If you host your .NET orchestrator on-device, you also inherit device operational constraints. Reachy Mini’s workflow docs show you have SSH access as `pollen@reachy-mini.local` and you can inspect network config, install code, etc. citeturn11view0turn11view1  

If you host your orchestrator off-device, Reachy Mini’s own core concepts page explicitly endorses that model: AI code on a powerful server, daemon on the Raspberry Pi. citeturn6view2

### RAG and knowledge bases: keep the heavy index off-device

Given limited flash and CPU, a maintainable pattern is:

- On-device: identity, preferences, “short memory,” and a cache of the last N retrieved documents/snippets  
- Off-device: vector store + ingestion pipeline + long-term knowledge base

OpenAI’s Retrieval guide describes retrieval as semantic search over your data, powered by **vector stores** that serve as indices, and frames retrieval as especially useful when combined with models to synthesize responses. citeturn10search5  
For ingestion, OpenAI’s cookbook includes concrete workflows for turning PDFs into usable content for a RAG pipeline. citeturn10search0  

This architecture is also aligned with OpenAI’s “server-side controls” guidance for Realtime: tool use and business logic usually live on your application server, not on a thin client device. citeturn9search2  

Maintainability benefits:
- you can evolve chunking/indexing without redeploying the robot
- you can do privacy and access control centrally
- you reduce device disk churn (logs and caches tend to be what eat small disks first)

## Are these patterns scalable for maintainable device code?

Yes—*if* you adopt the same “separation of concerns” that the platform itself uses, and you enforce backpressure and ownership boundaries so complexity can’t accumulate in one place. The strongest evidence is that Reachy Mini’s own official architecture and example apps already encode these scalability decisions: daemon/client separation, shared media services, tool-based interfaces, and profile-driven tool selection. citeturn6view2turn6view0turn12view2turn11view3  

The reason these patterns are scalable is that they scale along two different axes:

- **Scalability of performance**: bounded pipelines, backpressure, buffer pooling, and worker supervision keep latency stable even when workloads spike. Microsoft’s Channels + Dataflow primitives are explicitly intended for async producer–consumer and message-passing parallel pipelines. citeturn7search1turn7search2turn7search6turn7search7  
- **Scalability of codebase complexity**: plugin boundaries + declarative configuration reduce cross-cutting changes. The Reachy Mini conversation app’s tool/profile system is an existence proof that this approach can support many skills without turning into one monolith. citeturn12view2turn12view1  

If you keep too much on-device, it still can be maintainable, but you must treat your app like an embedded streaming system:
- Use a host + supervised workers pattern for lifecycle management. citeturn7search4turn7search0turn8search26  
- Use channels/dataflow with bounded buffers for every stream. citeturn7search1turn7search2  
- Design interruption and session rotation into your dialogue state machine from day one (Realtime sessions are finite and interruption flows are explicit). citeturn6view6turn9search0turn9search5  
- Keep the long-term KB/index off-device and retrieve over the network. citeturn10search5turn9search2  

Finally, you will not keep a multimodal system maintainable without observability. .NET’s OpenTelemetry guidance strongly emphasizes structured tracing/metrics primitives (`ActivitySource`, `Meter`) and best practices like creating sources once and keeping names unique. citeturn8search6turn8search2turn8search31  
On Reachy Mini specifically, the platform docs also encourage monitoring logs (`journalctl -u reachy-mini-daemon -f`) when changing daemon builds—this same operational posture (logs + restartability + health checks) is what keeps device code maintainable over time. citeturn11view1turn7search12