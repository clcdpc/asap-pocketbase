'use strict';
const net = require('node:net');

// Forward encrypted bytes unchanged, including the form_post OIDC callback.
// Chromium still verifies the localhost certificate and keeps the exact origin.
function forward(port = 8080, target = { host: 'web', port: 8443 }) {
  return ['127.0.0.1', '::1'].map(host => {
    const server = net.createServer(client => {
      const upstream = net.connect(target);
      client.on('error', () => upstream.destroy());
      upstream.on('error', () => client.destroy());
      client.on('close', () => upstream.destroy());
      upstream.on('close', () => client.destroy());
      client.pipe(upstream).pipe(client);
    });
    server.listen({ port, host, ipv6Only: host === '::1' });
    return server;
  });
}
if (require.main === module) forward();
module.exports = { forward };
