const { createBroker } = require('aedes');
const { createServer } = require('net');

async function main() {
  const broker = await createBroker();
  const server = createServer(broker.handle);
  const port = Number.parseInt(process.env.MQTT_PORT || '1883', 10);

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '0.0.0.0', resolve);
  });

  console.log(JSON.stringify({ event: 'ready', broker: 'aedes', port }));
  const shutdown = () => server.close(() => broker.close(() => process.exit(0)));
  process.once('SIGTERM', shutdown);
  process.once('SIGINT', shutdown);
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
