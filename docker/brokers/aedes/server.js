const aedes = require('aedes')();
const { createServer } = require('net');

createServer(aedes.handle).listen(1883, '0.0.0.0');
