const { JSDOM } = require("jsdom");
const doc = new JSDOM('<a href="java&#x09;script:alert(1)"></a>').window.document;
const a = doc.querySelector('a');
const value = a.attributes[0].value.trim().toLowerCase();
console.log("value:", value);
console.log("startsWith:", value.startsWith("javascript:"));
