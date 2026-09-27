import { importPKCS8, SignJWT, type CryptoKey } from 'jose';
import type { DotwireHostConfig, MintTokenOptions } from './types.js';

export class DotwireTokenSigner {
  private readonly config: DotwireHostConfig;
  private readonly keyPromise: Promise<CryptoKey>;
  private readonly defaultTtl: string;

  constructor(config: DotwireHostConfig) {
    this.config = config;
    this.keyPromise = importPKCS8(config.privateKeyPem, 'RS256');
    this.defaultTtl = config.defaultTokenTtl ?? '15m';
  }

  async mintToken(userId: string, options: MintTokenOptions = {}): Promise<string> {
    const key = await this.keyPromise;
    const jwt = new SignJWT({
      'dw:role': options.role ?? 'member',
      ...(options.displayName ? { 'dw:name': options.displayName } : {})
    })
      .setProtectedHeader({ alg: 'RS256', kid: this.config.keyId })
      .setIssuer(this.config.issuer)
      .setSubject(userId)
      .setAudience(this.config.audience ?? 'dotwire')
      .setIssuedAt()
      .setExpirationTime(options.ttl ?? this.defaultTtl);

    return jwt.sign(key);
  }
}
