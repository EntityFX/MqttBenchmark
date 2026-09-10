const { Aedes } = require('aedes');
const { createServer } = require('net');

async function main() {
  // Unlimited emitter concurrency avoids mqemitter's synchronous queued-release recursion under burst load.
  const broker = await Aedes.createBroker({ concurrency: 0 });
  const server = createServer(broker.handle);
  const port = Number.parseInt(process.env.MQTT_PORT || '1883', 10);

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
