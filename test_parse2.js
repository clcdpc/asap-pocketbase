const { JSDOM } = require("jsdom");
const doc = new JSDOM('<a href="   javascript:alert(1)"></a>').window.document;
const a = doc.querySelector('a');
console.log(a.attributes[0].value);
