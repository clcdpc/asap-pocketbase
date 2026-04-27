function utf8Bytes(str) {
  var bytes = [];
  str = String(str);
  for (var i = 0; i < str.length; i++) {
    var code = str.charCodeAt(i);
    if (code < 0x80) {
      bytes.push(code);
    } else if (code < 0x800) {
      bytes.push(0xc0 | (code >> 6));
      bytes.push(0x80 | (code & 0x3f));
    } else if (code >= 0xd800 && code <= 0xdbff) {
      i++;
      var next = str.charCodeAt(i);
      var cp = 0x10000 + (((code & 0x3ff) << 10) | (next & 0x3ff));
      bytes.push(0xf0 | (cp >> 18));
      bytes.push(0x80 | ((cp >> 12) & 0x3f));
      bytes.push(0x80 | ((cp >> 6) & 0x3f));
      bytes.push(0x80 | (cp & 0x3f));
    } else {
      bytes.push(0xe0 | (code >> 12));
      bytes.push(0x80 | ((code >> 6) & 0x3f));
      bytes.push(0x80 | (code & 0x3f));
    }
  }
  return bytes;
}

function rotl(value, bits) {
  return (value << bits) | (value >>> (32 - bits));
}

function sha1(bytes) {
  var ml = bytes.length * 8;
  var mlHigh = Math.floor(ml / 0x100000000);
  var mlLow = ml >>> 0;
  bytes = bytes.slice(0);
  bytes.push(0x80);
  while ((bytes.length % 64) !== 56) {
    bytes.push(0);
  }
  for (var i = 3; i >= 0; i--) {
    bytes.push((mlHigh >>> (i * 8)) & 0xff);
  }
  for (i = 3; i >= 0; i--) {
    bytes.push((mlLow >>> (i * 8)) & 0xff);
  }

  var h0 = 0x67452301;
  var h1 = 0xefcdab89;
  var h2 = 0x98badcfe;
  var h3 = 0x10325476;
  var h4 = 0xc3d2e1f0;
  var w = new Array(80);

  for (var offset = 0; offset < bytes.length; offset += 64) {
    for (var j = 0; j < 16; j++) {
      var k = offset + j * 4;
      w[j] = ((bytes[k] << 24) | (bytes[k + 1] << 16) | (bytes[k + 2] << 8) | bytes[k + 3]) >>> 0;
    }
    for (j = 16; j < 80; j++) {
      w[j] = rotl(w[j - 3] ^ w[j - 8] ^ w[j - 14] ^ w[j - 16], 1) >>> 0;
    }

    var a = h0;
    var b = h1;
    var c = h2;
    var d = h3;
    var e = h4;

    for (j = 0; j < 80; j++) {
      var f;
      var kt;
      if (j < 20) {
        f = (b & c) | ((~b) & d);
        kt = 0x5a827999;
      } else if (j < 40) {
        f = b ^ c ^ d;
        kt = 0x6ed9eba1;
      } else if (j < 60) {
        f = (b & c) | (b & d) | (c & d);
        kt = 0x8f1bbcdc;
      } else {
        f = b ^ c ^ d;
        kt = 0xca62c1d6;
      }
      var temp = (rotl(a, 5) + f + e + kt + w[j]) >>> 0;
      e = d;
      d = c;
      c = rotl(b, 30) >>> 0;
      b = a;
      a = temp;
    }

    h0 = (h0 + a) >>> 0;
    h1 = (h1 + b) >>> 0;
    h2 = (h2 + c) >>> 0;
    h3 = (h3 + d) >>> 0;
    h4 = (h4 + e) >>> 0;
  }

  var words = [h0, h1, h2, h3, h4];
  var out = [];
  for (i = 0; i < words.length; i++) {
    out.push((words[i] >>> 24) & 0xff);
    out.push((words[i] >>> 16) & 0xff);
    out.push((words[i] >>> 8) & 0xff);
    out.push(words[i] & 0xff);
  }
  return out;
}

function base64(bytes) {
  var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  var output = "";
  for (var i = 0; i < bytes.length; i += 3) {
    var b0 = bytes[i];
    var b1 = i + 1 < bytes.length ? bytes[i + 1] : 0;
    var b2 = i + 2 < bytes.length ? bytes[i + 2] : 0;
    var triple = (b0 << 16) | (b1 << 8) | b2;
    output += alphabet[(triple >> 18) & 0x3f];
    output += alphabet[(triple >> 12) & 0x3f];
    output += i + 1 < bytes.length ? alphabet[(triple >> 6) & 0x3f] : "=";
    output += i + 2 < bytes.length ? alphabet[triple & 0x3f] : "=";
  }
  return output;
}

function hmacSha1Base64(secret, message) {
  var key = utf8Bytes(secret);
  if (key.length > 64) {
    key = sha1(key);
  }
  while (key.length < 64) {
    key.push(0);
  }

  var ipad = [];
  var opad = [];
  for (var i = 0; i < 64; i++) {
    ipad.push(key[i] ^ 0x36);
    opad.push(key[i] ^ 0x5c);
  }
  var inner = sha1(ipad.concat(utf8Bytes(message)));
  return base64(sha1(opad.concat(inner)));
}

module.exports = {
  hmacSha1Base64: hmacSha1Base64,
  utf8Bytes: utf8Bytes,
};
