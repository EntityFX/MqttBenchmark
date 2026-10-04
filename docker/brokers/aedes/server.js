const { Aedes } = require('aedes');
const { createServer } = require('net');

async function main() {
  // Unlimited emitter concurrency avoids mqemitter's synchronous queued-release recursion under burst load.
  const broker = await Aedes.createBroker({ concurrency: 0 });
  const server = createServer(broker.handle);
  // Container runs get MQTT_PORT from the stand (compose environment). A native, Docker-free run
  // gets the port as the first CLI argument (`node server.js <port>`), because the stand controller
  // passes broker.args verbatim and has no way to inject environment variables portably.
  const port = Number.parseInt(process.argv[2] || process.env.MQTT_PORT || '1883', 10);
  if (!Number.isInteger(port) || port < 1 || port > 65535)
    throw new Error(`Invalid MQTT port: ${process.argv[2] ?? process.env.MQTT_PORT}`);

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '0.0.0.0', resolve);
  });

  // Routine connections and readiness are silent, matching the stand's warning/error policy.
  broker.on('clientError', (_client, error) => console.warn(error.message));
  server.on('error', error => console.error(error.message));
  const shutdown = () => server.close(() => broker.close(() => process.exit(0)));
  process.once('SIGTERM', shutdown);
  process.once('SIGINT', shutdown);
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
