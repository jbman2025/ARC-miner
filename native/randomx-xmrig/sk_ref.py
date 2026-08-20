import skein
data=bytes(range(200))
print(skein.skein512(data,digest_bits=256).hexdigest())
# sanity: empty vector
print("empty:",skein.skein512(b'',digest_bits=256).hexdigest())
