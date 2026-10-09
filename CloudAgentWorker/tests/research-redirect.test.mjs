import assert from "node:assert/strict";
import test from "node:test";
import worker from "../src/index.ts";

const token = "local-test-token";

function createRequest(overrides = {}) {
  return new Request("https://worker.test/api/chat", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      agentId: "researcher",
      message: "WPF user interface examples",
      searchQuery: "WPF user interface examples",
      ...overrides,
    }),
  });
}

function createEnv(runAi) {
  return {
    AGENT_API_TOKEN: token,
    AI: { run: runAi },
  };
}

test("Jarvis handles conversational questions without public research or action claims", async (t) => {
  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });
  globalThis.fetch = async () => {
    throw new Error("Jarvis must not trigger a public search.");
  };

  let messages;
  const response = await worker.fetch(
    createRequest({
      agentId: "jarvis",
      message: "Merhaba, nasıl yardımcı olabilirsin?",
      searchQuery: null,
    }),
    createEnv(async (_model, input) => {
      messages = input.messages;
      return { response: "Merhaba! Sorularınızı yanıtlayabilirim." };
    }),
    {},
  );
  const body = await response.json();

  assert.equal(response.status, 200);
  assert.match(messages[0].content, /Genel sorulara yanıt veren sohbet/);
  assert.match(messages[0].content, /ERP\/CRM veritabanına.*erişimin yoktur/);
  assert.match(messages[0].content, /Kayıt, müşteri iletişimi, ödeme, fatura/);
  assert.equal(body.response, "Merhaba! Sorularınızı yanıtlayabilirim.");
});

test("research uses Workers-compatible manual redirects and reports an unexpected redirect safely", async (t) => {
  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  let aiCalled = false;
  globalThis.fetch = async (_input, init) => {
    assert.equal(init.redirect, "manual");
    return new Response(null, {
      status: 302,
      headers: { location: "https://provider.test/private-redirect-target" },
    });
  };

  const response = await worker.fetch(
    createRequest(),
    createEnv(async () => {
      aiCalled = true;
      return { response: "unexpected" };
    }),
    {},
  );
  const body = await response.json();

  assert.equal(response.status, 502);
  assert.equal(body.phase, "public-research");
  assert.equal(body.code, "unexpected_redirect");
  assert.equal(body.upstreamStatus, 302);
  assert.equal(body.error.includes("private-redirect-target"), false);
  assert.equal(aiCalled, false);
});

test("research continues to Workers AI after a successful repository search", async (t) => {
  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  globalThis.fetch = async (_input, init) => {
    assert.equal(init.redirect, "manual");
    return Response.json({
      items: [
        {
          full_name: "example/public-ui",
          description: "Public interface sample",
          html_url: "https://github.com/example/public-ui",
          stargazers_count: 12,
        },
      ],
    });
  };

  let aiReceivedSearchResult = false;
  const response = await worker.fetch(
    createRequest(),
    createEnv(async (_model, input) => {
      aiReceivedSearchResult = input.messages[1].content.includes("example/public-ui");
      return { response: "Araştırma yanıtı" };
    }),
    {},
  );
  const body = await response.json();

  assert.equal(response.status, 200);
  assert.equal(body.response, "Araştırma yanıtı");
  assert.equal(aiReceivedSearchResult, true);
});

test("Workers AI failure remains an honest 502 and redacts secret-like diagnostics", async () => {
  const response = await worker.fetch(
    createRequest({
      agentId: "jarvis",
      message: "Kısa bir selamlama",
      searchQuery: null,
    }),
    createEnv(async () => {
      throw new Error("Authorization: Bearer hidden-token api_key=hidden-key");
    }),
    {},
  );
  const body = await response.json();

  assert.equal(response.status, 502);
  assert.equal(body.phase, "workers-ai");
  assert.equal(body.code, "upstream_error");
  assert.equal(body.error.includes("hidden-token"), false);
  assert.equal(body.error.includes("hidden-key"), false);
  assert.equal("response" in body, false);
});

test("GitHub Search 502 is classified as public research and AI is not called", async (t) => {
  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  globalThis.fetch = async () => new Response("sensitive upstream body", { status: 502 });
  let aiCalls = 0;
  const response = await worker.fetch(
    createRequest(),
    createEnv(async () => {
      aiCalls += 1;
      return { response: "unexpected" };
    }),
    {},
  );
  const body = await response.json();

  assert.equal(response.status, 502);
  assert.equal(body.phase, "public-research");
  assert.equal(body.code, "upstream_http_error");
  assert.equal(body.upstreamStatus, 502);
  assert.equal(body.error.includes("sensitive upstream body"), false);
  assert.equal(aiCalls, 0);
});

test("empty Workers AI completion is classified and does not trigger quota-consuming retries", async () => {
  let aiCalls = 0;
  let requestedMaxTokens;
  const response = await worker.fetch(
    createRequest({
      agentId: "jarvis",
      message: "Kısa bir selamlama",
      searchQuery: null,
    }),
    createEnv(async (_model, input) => {
      aiCalls += 1;
      requestedMaxTokens = input.max_tokens;
      return { choices: [{ finish_reason: "length", message: { content: "" } }] };
    }),
    {},
  );
  const body = await response.json();

  assert.equal(response.status, 502);
  assert.equal(body.phase, "workers-ai");
  assert.equal(body.code, "empty_completion");
  assert.equal(body.finishReason, "length");
  assert.equal(aiCalls, 1);
  assert.equal(requestedMaxTokens, 4_096);
  assert.equal("response" in body, false);
});
