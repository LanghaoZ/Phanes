# Phanes

**Phanes** is a personal execution platform designed for building and running AI-driven workflows and applications.

The project combines two core capabilities:

- a **general-purpose task runtime**
- a **local agent execution environment**

Phanes serves as the foundation for personal AI systems such as analytics assistants, automation workflows, and experimental agent tools.

---

# Goals

Phanes is designed with the following goals:

- Provide a **reusable task execution framework**
- Enable a **local-first AI agent environment**
- Support **sandboxed execution of agent-generated code**
- Power personal applications such as **OpenInsights**

The platform focuses on **developer productivity and experimentation**, allowing complex workflows to run safely on local infrastructure.

---

# Core Concepts

Phanes combines several components into a single execution platform.

## Task Runtime

The task runtime provides a general-purpose framework for asynchronous job execution.

Capabilities include:

- background job execution
- scheduling and cron-style tasks
- retries and failure handling
- worker-based execution
- plugin-style task implementations

This runtime is designed to be reusable across multiple personal projects.

---

## Agent Runtime

The agent runtime enables AI-driven workflows powered by LLMs.

Responsibilities include:

- tool orchestration
- AI-assisted coding workflows
- multi-step reasoning pipelines
- integration with external model APIs (via OpenRouter)

The agent runtime acts as the **intelligence layer** of the platform.

---

## Sandbox Execution

Phanes includes a sandbox layer for executing agent-generated code safely.

The sandbox environment supports workloads such as:

- Python data analysis
- SQL exploration
- report generation
- chart creation

Sandbox isolation ensures that AI-generated code runs safely without compromising the host system.

---

# Use Cases

Phanes is intended to support projects such as:

- personal AI assistants
- automated analytics systems
- workflow automation
- data exploration tools

The first application built on top of Phanes is **OpenInsights**, a personal financial insights service.

---

# Project Status

Phanes is an early-stage personal infrastructure project and is under active development.

The focus of the initial versions is:

- task runtime MVP
- agent runtime integration
- sandbox execution environment

Additional features will be introduced incrementally as the platform evolves.