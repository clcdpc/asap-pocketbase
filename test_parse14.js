const { JSDOM } = require("jsdom");
const doc = new JSDOM('<a href="vbscript:msgbox(1)"></a>').window.document;
const a = doc.querySelector('a');
const value = a.attributes[0].value.replace(/[\s\u0000-\u0020]/g, '').toLowerCase();
console.log("value:", value);
console.log("startsWith:", value.startsWith("javascript:"));
