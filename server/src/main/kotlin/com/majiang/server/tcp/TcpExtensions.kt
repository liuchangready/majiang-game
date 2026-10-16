package com.majiang.server.tcp

import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameEncoder

/**
 * 将 [TcpFrame] 便捷序列化为固定 12 字节头部 + 载荷的二进制字节数组
 */
fun TcpFrame.toByteArray(): ByteArray = TcpFrameEncoder.encode(type, payload, flags)
