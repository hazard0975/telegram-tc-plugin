const http = require('http');

const PORT = 3000;

const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(`
    <!DOCTYPE html>
    <html lang="ru">
    <head>
      <meta charset="UTF-8">
      <meta name="viewport" content="width=device-width, initial-scale=1.0">
      <title>TgVfsPlugin - C# Project</title>
      <style>
        body {
          font-family: system-ui, -apple-system, sans-serif;
          display: flex;
          align-items: center;
          justify-content: center;
          height: 100vh;
          margin: 0;
          background-color: #f3f4f6;
          color: #1f2937;
        }
        .card {
          background: white;
          padding: 2rem;
          border-radius: 8px;
          box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
          text-align: center;
          max-width: 500px;
        }
        h1 { margin-top: 0; }
        p { color: #4b5563; line-height: 1.5; }
      </style>
    </head>
    <body>
      <div class="card">
        <h1>TgVfsPlugin</h1>
        <p>Это проект на C# (WFX-плагин для Total Commander).</p>
        <p>Веб-сервер запущен только в качестве заглушки, чтобы среда AI Studio корректно работала.</p>
      </div>
    </body>
    </html>
  `);
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`Stub server running on port ${PORT}`);
});
