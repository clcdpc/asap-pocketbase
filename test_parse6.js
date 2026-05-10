const { JSDOM } = require("jsdom");
const doc = new JSDOM('<a href="   javascript:alert(1)"></a>').window.document;
const a = doc.querySelector('a');
const value = a.attributes[0].value.replace(/[\u0000-\u0020\u00A0\u1680\u180E\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]/g, '').toLowerCase();
console.log("value:", value);
console.log("startsWith:", value.startsWith("javascript:"));
