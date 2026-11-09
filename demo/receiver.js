// A small receiver for trying the webhook service on one computer. Not part of the service itself.
//
//   POST /ok    answers 200
//   POST /fail  answers 500, so the delivery is retried and finally gives up (dead)
//   POST /slow  answers after 15 seconds, which is longer than the service waits
//
// It prints each request with the headers that carry the signature, so you can see what a real receiver would
// check. It does not check the signature itself.

const http = require("node:http");

const PORT = Number(process.env.PORT ?? 9000);
const MAX_BODY = 1_000_000;

const server = http.createServer((request, response) => {
  let size = 0;
  const chunks = [];

  request.on("data", (chunk) => {
    size += chunk.length;
    if (size <= MAX_BODY) chunks.push(chunk);
  });

  request.on("end", () => {
    const body = Buffer.concat(chunks).toString("utf8");
    console.log(
      JSON.stringify({
        at: new Date().toISOString(),
        method: request.method,
        path: request.url,
        webhookId: request.headers["webhook-id"],
        webhookTimestamp: request.headers["webhook-timestamp"],
        webhookSignature: request.headers["webhook-signature"],
        body: body.slice(0, 500),
      }),
    );

    if (request.url === "/ok") return reply(response, 200, "ok");
    if (request.url === "/fail") return reply(response, 500, "this receiver always fails");
    if (request.url === "/slow") return void setTimeout(() => reply(response, 200, "late"), 15_000);
    return reply(response, 404, "use /ok, /fail or /slow");
  });
});

function reply(response, status, text) {
  response.writeHead(status, { "Content-Type": "text/plain" });
  response.end(text);
}

server.listen(PORT, "0.0.0.0", () => console.log(`receiver listening on ${PORT}`));
